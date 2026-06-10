using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Winche.Database.AspNetCore.DependencyInjection;
using Winche.Database.AspNetCore.Rest.DependencyInjection;
using Winche.Database.DependencyInjection;

namespace Winche.Database.IntegrationTests.Rest;

/// <summary>
/// Real ASP.NET host over the Testcontainers database, exercised via HttpClient.
/// Mirrors WsTestHost: same AddWincheDatabase + MapClaims wiring.
/// Test auth: no token → empty claims (no access control unless a rule is configured).
/// </summary>
public sealed class RestTestHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    public TestServer Server { get; }
    public HttpClient Client => Server.CreateClient();

    private RestTestHost(WebApplication app)
    {
        _app = app;
        Server = (TestServer)app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
    }

    public static async Task<RestTestHost> StartAsync(string connectionString,
        Action<WincheDatabaseOptions>? configureDb = null)
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

        var app = builder.Build();
        await app.InitializeWincheDatabaseAsync();
        app.MapWincheDatabaseRestApi();
        await app.StartAsync();
        return new RestTestHost(app);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
