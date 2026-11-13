using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    /// Authentication is performed at the HTTP upgrade: the app's authentication scheme validates
    /// the request (e.g. a query-string token surfaced by <see cref="UseWincheWsQueryToken"/>),
    /// and the resulting <c>HttpContext.User</c> provides the caller's identity for the entire
    /// connection lifetime. There is no in-band hello handshake or token refresh — if a token
    /// expires the client reconnects. <c>.RequireAuthorization()</c> on this builder is the
    /// recommended gate (rejects the upgrade before the socket is accepted); without it a missing
    /// or expired token yields an anonymous connection rather than a 401.
    /// </para>
    /// <para>
    /// To withdraw access from connections that are already open (a deleted or disabled user), call
    /// <see cref="WsConnectionRegistry.CloseUserConnectionsAsync"/>.
    /// </para>
    /// </summary>
    public static IEndpointConventionBuilder MapWincheDatabaseWsApi(this WebApplication app, string prefix = "documents/v1") =>
        app.Map($"/{prefix}/ws", HandleAsync);

    /// <summary>
    /// Adds middleware that promotes a WebSocket query-string token to an
    /// <c>Authorization: Bearer …</c> header so the app's authentication scheme can validate it
    /// at the HTTP upgrade. Must be placed <b>before</b> <c>UseAuthentication()</c>.
    /// <para>
    /// The library does NOT validate the token; validation is the responsibility of the
    /// app's registered authentication handler (issuer, keys, audience are all app-specific).
    /// </para>
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <param name="paramName">Query-string parameter name (default: <c>access_token</c>).</param>
    public static IApplicationBuilder UseWincheWsQueryToken(this IApplicationBuilder app, string paramName = "access_token") =>
        app.Use(async (context, next) =>
        {
            if (context.WebSockets.IsWebSocketRequest
                && !context.Request.Headers.ContainsKey("Authorization")
                && context.Request.Query.TryGetValue(paramName, out var token)
                && !string.IsNullOrEmpty(token))
            {
                context.Request.Headers["Authorization"] = $"Bearer {token}";
            }
            await next(context);
        });

    // The metric tag takes only these (a client-chosen string would let one client create unbounded series).
    private static readonly HashSet<string> KnownTypes = new(StringComparer.Ordinal) { "ping", "doc.get", "doc.getAll", "query", "count", "aggregate", "add", "write", "tx.begin", "tx.get", "tx.query", "tx.commit", "tx.rollback", "listen", "doc.listen", "unlisten" };

    private static async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var options = context.RequestServices.GetRequiredService<IOptions<WsOptions>>().Value;
        var router = context.RequestServices.GetRequiredService<MessageRouter>();
        var db = context.RequestServices.GetRequiredService<IDocumentDatabase>();
        var claimsAccessor = context.RequestServices.GetRequiredService<DocumentClaimsAccessor>();
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("WincheWs");

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        await using var conn = new WsConnection(socket, options);
        Winche.Database.Runtime.WincheDatabaseMetrics.WsConnections.Add(1);
        using var sendLoopCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, conn.Closed);
        var sendLoop = conn.RunSendLoopAsync(sendLoopCts.Token);

        var userId = context.User.FindFirst(options.UserIdClaimType)?.Value
                     ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        using var registration = userId is null
            ? null
            : context.RequestServices.GetService<WsConnectionRegistry>()?.Register(userId,
                (code, reason) => conn.RevokeAsync(sendLoop, (WebSocketCloseStatus)code, reason, TimeSpan.FromSeconds(5)),
                options.MaxConnectionsPerUser);

        var scope = new ConnectionScope(Guid.NewGuid().ToString("N"), db, claimsAccessor)
        {
            MaxSubscriptions = options.MaxSubscriptionsPerConnection,
        };
        var throttle = new RequestThrottle(options.RequestsPerSecond, options.RequestBurst);
        try
        {
            // ── authenticate from the already-validated HttpContext.User ─────────
            var claims = claimsAccessor.MapClaims(context);
            scope.SetClaims(claims ?? new Dictionary<string, object?>());

            // ── server-initiated welcome (no client hello frame) ─────────────────
            conn.TrySend(new WelcomeMessage { ConnectionId = scope.ConnectionId });

            // ── serial message loop ───────────────────────────────────────────────
            while (!context.RequestAborted.IsCancellationRequested)
            {
                var doc = await conn.ReceiveAsync(context.RequestAborted);
                if (doc is null) break;
                if (conn.Closed.IsCancellationRequested)
                {
                    // Closing (revoked, or the send queue overflowed): nothing more runs under this identity.
                    doc.Dispose();
                    break;
                }

                ClientMessage? message;
                using (doc)
                {
                    var type = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("type", out var t)
                               && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                    Winche.Database.Runtime.WincheDatabaseMetrics.WsRequests.Add(1, Winche.Database.Runtime.WincheDatabaseMetrics.Tags("type", type is not null && KnownTypes.Contains(type) ? type : "other"));
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

                // Over the request rate: handle it later rather than reject it (see WsOptions.RequestsPerSecond).
                await throttle.WaitAsync(context.RequestAborted);
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
            Winche.Database.Runtime.WincheDatabaseMetrics.WsConnections.Add(-1);
            await scope.DisposeAsync();
            await conn.DrainAndCloseAsync(sendLoop, WebSocketCloseStatus.NormalClosure, "bye");
        }
    }
}
