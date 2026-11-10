using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Winche.Database.AspNetCore.WebSockets.Connections;
using Winche.Rules;
using Winche.Rules.Expressions;

namespace Winche.Database.IntegrationTests.Ws;

/// <summary>10.5.0: per-connection subscription cap, per-user connection cap, per-connection request rate.</summary>
[Collection("postgres")]
public class WsLimitsTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private Task<WsTestHost> Host(Action<WsOptions> configure) =>
        WsTestHost.StartAsync(Fx.ConnectionString,
            // Reads allowed everywhere, so only the limits under test can refuse a listen.
            configureDb: c => c.UseRules(r => r.Match("{path=**}", m => m.Allow(RuleOperations.Read, Expr.Const(true)))),
            configureWs: o => { o.UserIdClaimType = "uid"; configure(o); }, requireAuth: true);

    private static JsonObject Listen(string collection) =>
        new() { ["type"] = "listen", ["query"] = new JsonObject { ["collection"] = collection } };

    private static JsonObject DocListen(string path) => new() { ["type"] = "doc.listen", ["path"] = path };

    private static JsonObject Unlisten(string id) => new() { ["type"] = "unlisten", ["subscriptionId"] = id };

    [Fact]
    public async Task A_connection_may_hold_only_so_many_subscriptions()
    {
        await using var host = await Host(o => o.MaxSubscriptionsPerConnection = 2);
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server, "uid:alice");

        var first = await ws.RequestAsync(Listen("lim"));
        Assert.Equal("response", (string?)first["type"]);
        Assert.Equal("response", (string?)(await ws.RequestAsync(DocListen("lim/a")))["type"]);

        var third = await ws.RequestAsync(Listen("lim2"));
        Assert.Equal("error", (string?)third["type"]);
        Assert.Equal("RESOURCE_EXHAUSTED", (string?)third["status"]);

        // Still open, and freeing one makes room.
        await ws.RequestAsync(Unlisten((string)first["result"]!["subscriptionId"]!));
        Assert.Equal("response", (string?)(await ws.RequestAsync(Listen("lim2")))["type"]);
    }

    [Fact]
    public async Task A_users_extra_connection_closes_their_oldest()
    {
        await using var host = await Host(o => o.MaxConnectionsPerUser = 2);
        var registry = host.Server.Services.GetRequiredService<WsConnectionRegistry>();
        await using var a1 = await WsTestClient.ConnectAndWelcomeAsync(host.Server, "uid:alice");
        await using var a2 = await WsTestClient.ConnectAndWelcomeAsync(host.Server, "uid:alice");
        await using var bob = await WsTestClient.ConnectAndWelcomeAsync(host.Server, "uid:bob");
        await using var a3 = await WsTestClient.ConnectAndWelcomeAsync(host.Server, "uid:alice");

        await a1.WaitForCloseAsync();
        Assert.Equal((WebSocketCloseStatus)WsConnectionRegistry.TooManyConnectionsCloseCode, a1.CloseStatus);
        Assert.Equal("response", (string?)(await a2.RequestAsync(new JsonObject { ["type"] = "ping" }))["type"]);
        Assert.Equal("response", (string?)(await a3.RequestAsync(new JsonObject { ["type"] = "ping" }))["type"]);
        Assert.Equal("response", (string?)(await bob.RequestAsync(new JsonObject { ["type"] = "ping" }))["type"]);
        Assert.Equal(2, registry.LocalConnectionCount("alice"));
    }

    [Fact]
    public async Task Over_the_request_rate_a_connection_is_slowed_never_refused()
    {
        await using var host = await Host(o => { o.RequestsPerSecond = 10; o.RequestBurst = 2; });
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server, "uid:alice");

        var watch = Stopwatch.StartNew();
        var ids = new List<string>();
        for (var i = 0; i < 12; i++)
        {
            var id = $"p{i}";
            ids.Add(id);
            await ws.SendAsync(new JsonObject { ["type"] = "ping", ["id"] = id });
        }
        foreach (var id in ids)
            Assert.Equal("response", (string?)(await ws.WaitForAsync(f => (string?)f["id"] == id))["type"]);
        watch.Stop();

        // 2 at once, then 10 more at 10/s: about a second.
        Assert.InRange(watch.Elapsed.TotalSeconds, 0.8, 5);
    }

    [Fact]
    public async Task Zero_turns_each_limit_off()
    {
        await using var host = await Host(o => { o.MaxSubscriptionsPerConnection = 0; o.RequestsPerSecond = 0; });
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server, "uid:alice");
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 120; i++)
            Assert.Equal("response", (string?)(await ws.RequestAsync(Listen($"off{i}")))["type"]);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
    }
}
