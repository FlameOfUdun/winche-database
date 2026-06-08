using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Winche.Database.AspNetCore.Abstraction;
using Winche.Database.AspNetCore.DependencyInjection;
using Winche.Database.AspNetCore.WebSockets.Connections;
using Winche.Database.AspNetCore.WebSockets.DependencyInjection;
using Winche.Database.DependencyInjection;

namespace Winche.Database.IntegrationTests.Ws;

/// <summary>
/// Real ASP.NET host over the Testcontainers database, exercised through TestServer's
/// WebSocket client. Hosted services (feed pump, pruner, sweeper) run for real.
/// Test auth: hello.token "uid:xyz" → claims {"uid":"xyz"}; null token → empty claims;
/// token "deny" → rejected (4401 path).
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

    public static async Task<WsTestHost> StartAsync(string connectionString,
        Action<WincheDatabaseOptions>? configureDb = null,
        Action<WsOptions>? configureWs = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddWincheDatabase(config =>
        {
            config.ConnectionString = connectionString;
            config.SetCallerClaimsAccessor<TestClaimsAccessor>();
            configureDb?.Invoke(config);
        });
        builder.Services.AddWincheDatabaseWsApi(configureWs);
        builder.Services.AddSingleton<IWsAuthenticator, TestWsAuthenticator>();   // overrides the default

        var app = builder.Build();
        await app.InitializeWincheDatabaseAsync();
        app.UseWebSockets();
        app.MapWincheDatabaseWsApi();
        await app.StartAsync();
        return new WsTestHost(app);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

public sealed class TestClaimsAccessor : DocumentClaimsAccessor
{
    public override IReadOnlyDictionary<string, object?> MapClaims(HttpContext httpContext) =>
        new Dictionary<string, object?>();
}

internal sealed class TestWsAuthenticator : IWsAuthenticator
{
    public ValueTask<IReadOnlyDictionary<string, object?>> AuthenticateAsync(
        HttpContext context, string? token, CancellationToken ct)
    {
        if (token == "deny")
            throw new UnauthorizedAccessException("denied");
        IReadOnlyDictionary<string, object?> claims = token?.StartsWith("uid:") == true
            ? new Dictionary<string, object?> { ["uid"] = token[4..] }
            : new Dictionary<string, object?>();
        return ValueTask.FromResult(claims);
    }
}
