using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Winche.Database.Abstraction;
using Winche.Database.AspNetCore.DependencyInjection;
using Winche.Database.AspNetCore.Rest.DependencyInjection;
using Winche.Database.DependencyInjection;
using Winche.Database.IntegrationTests.Ws;
using Winche.Sentinel.Models;

namespace Winche.Database.IntegrationTests.Rest;

/// <summary>
/// Real ASP.NET host over the Testcontainers database, exercised via HttpClient.
/// Mirrors WsTestHost: same AddWincheDatabase + TestClaimsAccessor wiring.
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
            config.SetCallerClaimsAccessor<TestClaimsAccessor>();
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

/// <summary>Allows all writes, deletes, and reads — used by REST tests that don't exercise access control.</summary>
internal sealed class RestAllowAllRule : DocumentAccessRule
{
    public override string Path => "**";
    public override IReadOnlySet<AccessOperation> Operations =>
        new HashSet<AccessOperation> { AccessOperation.Write, AccessOperation.Delete, AccessOperation.Read };

    public override Task<bool> EvaluateAsync(AccessContext<Documents.Document> context, CancellationToken ct) =>
        Task.FromResult(true);
}
