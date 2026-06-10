using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Winche.Database.AspNetCore.WebSockets.Connections;

namespace Winche.Database.IntegrationTests.Ws;

// NOTE: A 1013 send-queue overflow test is deliberately omitted — deterministically
// forcing a 64-frame backlog through TestServer is too timing-fragile to be reliable.

[Collection("postgres")]
public class WsLifecycleTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private Task<WsTestHost> Host() => WsTestHost.StartAsync(Fx.ConnectionString);

    // ── Happy path: connect → server welcome → ping/pong ────────────────────
    [Fact]
    public async Task Connect_Welcome_Ping()
    {
        await using var host = await Host();
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server);
        var pong = await ws.RequestAsync(new JsonObject { ["type"] = "ping" });
        Assert.Equal("response", (string?)pong["type"]);
    }

    // ── Welcome frame carries connectionId ───────────────────────────────────
    [Fact]
    public async Task Connect_WelcomeFrame_HasConnectionId()
    {
        await using var host = await Host();
        var ws = await WsTestClient.ConnectAsync(host.Server);
        var welcome = await ws.WaitForAsync(f => (string?)f["type"] == "welcome");
        Assert.False(string.IsNullOrEmpty((string?)welcome["connectionId"]));
        await ws.DisposeAsync();
    }

    // ── Unauthenticated upgrade is rejected when RequireAuthorization is set ─
    [Fact]
    public async Task UnauthenticatedUpgrade_Rejected_WhenRequireAuthorizationConfigured()
    {
        await using var host = await WsTestHost.StartAsync(Fx.ConnectionString, requireAuth: true);
        // No token → auth handler returns NoResult → 401 → WebSocket upgrade is refused
        // TestServer throws on ConnectAsync when the upgrade is rejected with a non-101 status
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await WsTestClient.ConnectAsync(host.Server));
    }

    // ── Authenticated upgrade succeeds; claims are the token identity ────────
    [Fact]
    public async Task AuthenticatedUpgrade_WelcomeReceived()
    {
        await using var host = await WsTestHost.StartAsync(Fx.ConnectionString, requireAuth: true);
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server, "uid:alice");
        var pong = await ws.RequestAsync(new JsonObject { ["type"] = "ping" });
        Assert.Equal("response", (string?)pong["type"]);
    }

    // ── C1: typeless frame mid-connection ────────────────────────────────────
    [Fact]
    public async Task TypelessFrame_ErrorFrame_ConnectionSurvives()
    {
        await using var host = await Host();
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server);
        await ws.SendAsync(new JsonObject { ["id"] = "q1" });                     // no "type"
        var err = await ws.WaitForAsync(f => (string?)f["type"] == "error");
        Assert.Equal("INVALID_ARGUMENT", (string?)err["status"]);
        var pong = await ws.RequestAsync(new JsonObject { ["type"] = "ping" });   // still alive
        Assert.Equal("response", (string?)pong["type"]);
    }

    // ── I3.2: oversized frame → close 4413 ──────────────────────────────────
    [Fact]
    public async Task OversizedFrame_Closes4413()
    {
        await using var host = await WsTestHost.StartAsync(Fx.ConnectionString,
            configureWs: o => o.MaxFrameBytes = 1024);
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server);
        // frame with a 4 KB string value
        await ws.SendAsync(new JsonObject { ["type"] = "ping", ["id"] = "x", ["pad"] = new string('A', 4096) });
        await ws.WaitForCloseAsync();
        Assert.Equal((WebSocketCloseStatus)4413, ws.CloseStatus);
    }

    // ── I3.3: binary frame → close 4400 ─────────────────────────────────────
    [Fact]
    public async Task BinaryFrame_Closes4400()
    {
        await using var host = await Host();
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server);
        await ws.SendBinaryAsync([0x01, 0x02, 0x03]);
        await ws.WaitForCloseAsync();
        Assert.Equal((WebSocketCloseStatus)4400, ws.CloseStatus);
    }

    // ── I3.4: malformed JSON mid-connection → error frame, connection survives ──
    [Fact]
    public async Task MalformedJson_ErrorFrame_ConnectionSurvives()
    {
        await using var host = await Host();
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server);
        await ws.SendRawAsync("{not json");
        var err = await ws.WaitForAsync(f => (string?)f["type"] == "error");
        Assert.Equal("INVALID_ARGUMENT", (string?)err["status"]);
        var pong = await ws.RequestAsync(new JsonObject { ["type"] = "ping" });
        Assert.Equal("response", (string?)pong["type"]);
    }
}
