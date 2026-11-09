using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Winche.Database.AspNetCore.WebSockets.Connections;

namespace Winche.Database.IntegrationTests.Ws;

/// <summary>
/// A connection keeps the identity it was opened with; revoking a user closes their open connections,
/// on this node and on others (via Postgres NOTIFY), and leaves everyone else's alone.
/// </summary>
[Collection("postgres")]
public class WsRevocationTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private Task<WsTestHost> Host() =>
        WsTestHost.StartAsync(Fx.ConnectionString, configureWs: o => o.UserIdClaimType = "uid", requireAuth: true);

    private static WsConnectionRegistry Registry(WsTestHost host) =>
        host.Server.Services.GetRequiredService<WsConnectionRegistry>();

    private static async Task Eventually(Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task Revoking_a_user_closes_their_connections_with_4401_and_spares_others()
    {
        await using var host = await Host();
        await using var alice1 = await WsTestClient.ConnectAndWelcomeAsync(host.Server, "uid:alice");
        await using var alice2 = await WsTestClient.ConnectAndWelcomeAsync(host.Server, "uid:alice");
        await using var bob = await WsTestClient.ConnectAndWelcomeAsync(host.Server, "uid:bob");
        Assert.Equal(2, Registry(host).LocalConnectionCount("alice"));

        await Registry(host).CloseUserConnectionsAsync("alice");

        await alice1.WaitForCloseAsync();
        await alice2.WaitForCloseAsync();
        Assert.Equal((WebSocketCloseStatus)WsConnectionRegistry.RevokedCloseCode, alice1.CloseStatus);
        Assert.Equal((WebSocketCloseStatus)WsConnectionRegistry.RevokedCloseCode, alice2.CloseStatus);
        await Eventually(() => Registry(host).LocalConnectionCount("alice") == 0);

        var pong = await bob.RequestAsync(new JsonObject { ["type"] = "ping" });
        Assert.Equal("response", (string?)pong["type"]);
        Assert.Equal(1, Registry(host).LocalConnectionCount("bob"));
    }

    [Fact]
    public async Task Revoking_on_one_node_closes_the_connection_held_by_another()
    {
        await using var nodeA = await Host();
        await using var nodeB = await Host();
        await using var alice = await WsTestClient.ConnectAndWelcomeAsync(nodeB.Server, "uid:alice");
        await Task.Delay(500); // let node B's listener start

        // The webhook lands on node A, which has no connection for alice.
        await Registry(nodeA).CloseUserConnectionsAsync("alice", "user deleted");

        await alice.WaitForCloseAsync();
        Assert.Equal((WebSocketCloseStatus)WsConnectionRegistry.RevokedCloseCode, alice.CloseStatus);
    }
}
