using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Winche.Database.AspNetCore.Abstraction;
using Winche.Database.AspNetCore.WebSockets.Connections;
using Winche.Database.AspNetCore.WebSockets.Protocol;
using Winche.Database.AspNetCore.WebSockets.Routing;
using Winche.Database.Runtime;
using Winche.Database.Wire;

namespace Winche.Database.AspNetCore.WebSockets.DependencyInjection;

public static class WebApplicationExtensions
{
    /// <summary>
    /// Maps the WebSocket endpoint at <c>/{prefix}/ws</c> and returns its
    /// <see cref="IEndpointConventionBuilder"/> so callers can apply conventions to the upgrade route
    /// (e.g. rate limiting, CORS). The caller must call <c>app.UseWebSockets()</c> before mapping.
    /// <para>
    /// Connection authentication is performed <b>in-band</b>: the token in the client's <c>hello</c>
    /// frame is validated by <see cref="IWsAuthenticator"/> during the handshake, and its claims feed
    /// the access guard. <c>.RequireAuthorization()</c> on this builder gates the HTTP <i>upgrade</i>
    /// against <c>HttpContext.User</c> instead — it is independent of the hello token and only an
    /// optional defense-in-depth layer, usable only with a scheme that authenticates the upgrade
    /// request itself (cookies or a query-string token; browsers cannot set a bearer header on a WS
    /// upgrade).
    /// </para>
    /// </summary>
    public static IEndpointConventionBuilder MapWincheDatabaseWsApi(this WebApplication app, string prefix = "documents") =>
        app.Map($"/{prefix}/ws", HandleAsync);

    private static async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var options = context.RequestServices.GetRequiredService<WsOptions>();
        var router = context.RequestServices.GetRequiredService<MessageRouter>();
        var authenticator = context.RequestServices.GetRequiredService<IWsAuthenticator>();
        var db = context.RequestServices.GetRequiredService<IDocumentDatabase>();
        var claimsAccessor = context.RequestServices.GetRequiredService<DocumentClaimsAccessor>();
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("WincheWs");

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        await using var conn = new WsConnection(socket, options);
        using var sendLoopCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, conn.Closed);
        var sendLoop = conn.RunSendLoopAsync(sendLoopCts.Token);

        ConnectionScope? scope = null;
        try
        {
            // ── hello handshake (spec §1) ─────────────────────────────────────
            using var helloCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            helloCts.CancelAfter(options.HelloTimeout);

            JsonDocument? first;
            try { first = await conn.ReceiveAsync(helloCts.Token); }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            {
                await conn.DrainAndCloseAsync(sendLoop, (WebSocketCloseStatus)4408, "hello timeout");
                return;
            }
            if (first is null) return;

            HelloMessage? hello;
            using (first)
            {
                try
                {
                    hello = JsonSerializer.Deserialize<ClientMessage>(first) as HelloMessage;
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException)
                {
                    hello = null;
                }
            }
            if (hello is null || hello.Protocol != 3)
            {
                conn.TrySend(new ErrorMessage { Status = "INVALID_ARGUMENT", Message = "Expected hello with protocol 3." });
                await conn.DrainAndCloseAsync(sendLoop, (WebSocketCloseStatus)4400, "protocol violation");
                return;
            }

            IReadOnlyDictionary<string, object?> claims;
            try
            {
                claims = await authenticator.AuthenticateAsync(context, hello.Token, context.RequestAborted);
            }
            catch (UnauthorizedAccessException ex)
            {
                conn.TrySend(new ErrorMessage { Status = "UNAUTHENTICATED", Message = ex.Message });
                await conn.DrainAndCloseAsync(sendLoop, (WebSocketCloseStatus)4401, "authentication failed");
                return;
            }

            scope = new ConnectionScope(Guid.NewGuid().ToString("N"), db, claimsAccessor);
            scope.SetClaims(claims);
            conn.TrySend(new WelcomeMessage { ConnectionId = scope.ConnectionId });

            // ── serial message loop ───────────────────────────────────────────
            while (!context.RequestAborted.IsCancellationRequested)
            {
                var doc = await conn.ReceiveAsync(context.RequestAborted);
                if (doc is null) break;

                ClientMessage? message;
                using (doc)
                {
                    try
                    {
                        message = JsonSerializer.Deserialize<ClientMessage>(doc);
                    }
                    catch (Exception ex) when (ex is JsonException or NotSupportedException)
                    {
                        string? msgId = doc.RootElement.TryGetProperty("id", out var idProp)
                                        && idProp.ValueKind == JsonValueKind.String ? idProp.GetString() : null;
                        var mapped = ErrorMapper.Map(ex);
                        conn.TrySend(new ErrorMessage
                        {
                            Id = msgId,
                            Status = mapped.Status,
                            Message = mapped.Message,
                            Details = mapped.Details,
                        });
                        continue;
                    }
                }
                if (message is null) continue;

                conn.TrySend(await router.HandleAsync(scope, conn, context, message, context.RequestAborted));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "WS connection failed");
        }
        finally
        {
            if (scope is not null) await scope.DisposeAsync();
            await conn.DrainAndCloseAsync(sendLoop, WebSocketCloseStatus.NormalClosure, "bye");
        }
    }
}
