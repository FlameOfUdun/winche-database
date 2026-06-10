using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Winche.Database.AspNetCore.DependencyInjection;
using Winche.Database.AspNetCore.WebSockets.Connections;
using Winche.Database.AspNetCore.WebSockets.DependencyInjection;
using Winche.Database.DependencyInjection;

namespace Winche.Database.IntegrationTests.Ws;

/// <summary>
/// Real ASP.NET host over the Testcontainers database, exercised through TestServer's
/// WebSocket client. Hosted services (feed pump, pruner, sweeper) run for real.
///
/// Auth model: <c>UseWincheWsQueryToken</c> promotes <c>?access_token=…</c> to a Bearer
/// header; <c>TestAuthHandler</c> validates it (token "uid:xyz" → claims {"uid":"xyz"};
/// any other non-empty token → authenticated but no uid; no token → unauthenticated).
/// <c>RequireAuthorization</c> on the WS endpoint rejects unauthenticated upgrades.
/// <c>MapClaims</c> maps <c>HttpContext.User</c> claims → the claims dict.
/// </summary>
public sealed class WsTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    public TestServer Server { get; }

    private WsTestHost(WebApplication app)
    {
        _app = app;
        Server = (TestServer)app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
    }

    public static async Task<WsTestHost> StartAsync(
        string connectionString,
        Action<WincheDatabaseOptions>? configureDb = null,
        Action<WsOptions>? configureWs = null,
        bool requireAuth = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddWincheDatabase(config =>
        {
            config.ConnectionString = connectionString;
            config.MapClaims(http =>
            {
                var uid = http.User.FindFirstValue("uid");
                return uid is not null
                    ? new Dictionary<string, object?> { ["uid"] = uid }
                    : new Dictionary<string, object?>();
            });
            configureDb?.Invoke(config);
        });
        builder.Services.AddWincheDatabaseWsApi(configureWs);

        // Test authentication scheme: validates the Bearer token set by UseWincheWsQueryToken
        builder.Services
            .AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();

        var app = builder.Build();
        await app.InitializeWincheDatabaseAsync();
        app.UseWincheWsQueryToken();    // must come before UseAuthentication
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseWebSockets();

        var wsEndpoint = app.MapWincheDatabaseWsApi();
        if (requireAuth)
            wsEndpoint.RequireAuthorization();

        await app.StartAsync();
        return new WsTestHost(app);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>
/// Minimal authentication handler for the WebSocket upgrade test.
/// Token format: "uid:xyz" → authenticated with uid=xyz claim.
/// Any other non-empty token → authenticated but no uid.
/// Missing/empty token → no result (unauthenticated).
/// </summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TestScheme";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authHeader = Request.Headers.Authorization.ToString();
        if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());

        var token = authHeader["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(token))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim> { new(ClaimTypes.Name, token) };
        if (token.StartsWith("uid:", StringComparison.Ordinal))
            claims.Add(new Claim("uid", token[4..]));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
