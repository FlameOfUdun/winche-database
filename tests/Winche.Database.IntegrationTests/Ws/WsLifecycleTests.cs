using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Winche.Database.AspNetCore.WebSockets.Connections;

namespace Winche.Database.IntegrationTests.Ws;

// NOTE: A 1013 send-queue overflow test is deliberately omitted — deterministically
// forcing a 64-frame backlog through TestServer is too timing-fragile to be reliable.

[Collection("postgres")]
public class WsLifecycleTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private Task<WsTestHost> Host() => WsTestHost.StartAsync(Fx.ConnectionString);

    [Fact]
    public async Task Hello_Welcome_Ping()
    {
        await using var host = await Host();
        await using var ws = await WsTestClient.ConnectV3Async(host.Server);
        var pong = await ws.RequestAsync(new JsonObject { ["type"] = "ping" });
        Assert.Equal("response", (string?)pong["type"]);
    }

    [Fact]
    public async Task WrongProtocol_Closes4400()
    {
        await using var host = await Host();
        await using var ws = await WsTestClient.ConnectAsync(host.Server);
        await ws.SendAsync(new JsonObject { ["type"] = "hello", ["protocol"] = 2 });
        await ws.WaitForAsync(f => (string?)f["type"] == "error");
        await ws.WaitForCloseAsync();
        Assert.Equal((WebSocketCloseStatus)4400, ws.CloseStatus);
    }

    [Fact]
    public async Task BadToken_Closes4401()
    {
        await using var host = await Host();
        await using var ws = await WsTestClient.ConnectAsync(host.Server);
        await ws.SendAsync(new JsonObject { ["type"] = "hello", ["protocol"] = 3, ["token"] = "deny" });
        await ws.WaitForAsync(f => (string?)f["type"] == "error" && (string?)f["status"] == "UNAUTHENTICATED");
        await ws.WaitForCloseAsync();
        Assert.Equal((WebSocketCloseStatus)4401, ws.CloseStatus);
    }

    [Fact]
    public async Task NonHelloFirstFrame_Closes4400()
    {
        await using var host = await Host();
        await using var ws = await WsTestClient.ConnectAsync(host.Server);
        await ws.SendAsync(new JsonObject { ["type"] = "ping", ["id"] = "1" });
        await ws.WaitForCloseAsync();
        Assert.Equal((WebSocketCloseStatus)4400, ws.CloseStatus);
    }

    [Fact]
    public async Task AuthRefresh_Succeeds_And_DenyFails()
    {
        await using var host = await Host();
        await using var ws = await WsTestClient.ConnectV3Async(host.Server, "uid:alice");
        var ok = await ws.RequestAsync(new JsonObject { ["type"] = "auth.refresh", ["token"] = "uid:bob" });
        Assert.Equal("response", (string?)ok["type"]);
        var bad = await ws.RequestAsync(new JsonObject { ["type"] = "auth.refresh", ["token"] = "deny" });
        Assert.Equal("UNAUTHENTICATED", (string?)bad["status"]);
    }

    // ── C1: typeless frame mid-connection ────────────────────────────────────
    [Fact]
    public async Task TypelessFrame_ErrorFrame_ConnectionSurvives()
    {
        await using var host = await Host();
        await using var ws = await WsTestClient.ConnectV3Async(host.Server);
        await ws.SendAsync(new JsonObject { ["id"] = "q1" });                     // no "type"
        var err = await ws.WaitForAsync(f => (string?)f["type"] == "error");
        Assert.Equal("INVALID_ARGUMENT", (string?)err["status"]);
        var pong = await ws.RequestAsync(new JsonObject { ["type"] = "ping" });   // still alive
        Assert.Equal("response", (string?)pong["type"]);
    }

    // ── I2: garbage first frame → error frame + close 4400 ──────────────────
    [Fact]
    public async Task GarbageFirstFrame_ErrorFrame_Closes4400()
    {
        await using var host = await Host();
        await using var ws = await WsTestClient.ConnectAsync(host.Server);
        await ws.SendAsync(new JsonObject { ["type"] = "bogus" });
        await ws.WaitForAsync(f => (string?)f["type"] == "error");
        await ws.WaitForCloseAsync();
        Assert.Equal((WebSocketCloseStatus)4400, ws.CloseStatus);
    }

    // ── I3.1: hello timeout → close 4408 ────────────────────────────────────
    [Fact]
    public async Task HelloTimeout_Closes4408()
    {
        await using var host = await WsTestHost.StartAsync(Fx.ConnectionString,
            configureWs: o => o.HelloTimeout = TimeSpan.FromMilliseconds(300));
        await using var ws = await WsTestClient.ConnectAsync(host.Server);
        // send nothing — timeout fires
        await ws.WaitForCloseAsync(timeoutMs: 5000);
        Assert.Equal((WebSocketCloseStatus)4408, ws.CloseStatus);
    }

    // ── I3.2: oversized frame → close 4413 ──────────────────────────────────
    [Fact]
    public async Task OversizedFrame_Closes4413()
    {
        await using var host = await WsTestHost.StartAsync(Fx.ConnectionString,
            configureWs: o => o.MaxFrameBytes = 1024);
        await using var ws = await WsTestClient.ConnectV3Async(host.Server);
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
        await using var ws = await WsTestClient.ConnectV3Async(host.Server);
        await ws.SendBinaryAsync([0x01, 0x02, 0x03]);
        await ws.WaitForCloseAsync();
        Assert.Equal((WebSocketCloseStatus)4400, ws.CloseStatus);
    }

    // ── I3.4: malformed JSON mid-connection → error frame, connection survives ──
    [Fact]
    public async Task MalformedJson_ErrorFrame_ConnectionSurvives()
    {
        await using var host = await Host();
        await using var ws = await WsTestClient.ConnectV3Async(host.Server);
        await ws.SendRawAsync("{not json");
        var err = await ws.WaitForAsync(f => (string?)f["type"] == "error");
        Assert.Equal("INVALID_ARGUMENT", (string?)err["status"]);
        var pong = await ws.RequestAsync(new JsonObject { ["type"] = "ping" });
        Assert.Equal("response", (string?)pong["type"]);
    }
}
