using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Winche.Database.AspNetCore.WebSockets.Connections;
using Winche.Rules;
using Winche.Rules.Expressions;

namespace Winche.Database.IntegrationTests.Ws;

// NOTE: A 1013 send-queue overflow test is deliberately omitted — deterministically
// forcing a 64-frame backlog through TestServer is too timing-fragile to be reliable.

[Collection("postgres")]
public class WsLifecycleTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private Task<WsTestHost> Host() => WsTestHost.StartAsync(Fx.ConnectionString);

    private Task<WsTestHost> AllowAllHost(Action<WsOptions> configureWs) => WsTestHost.StartAsync(Fx.ConnectionString,
        c => c.UseRules(r => r.Match("{document=**}", b => b.Allow(RuleOperations.All, Expr.Const(true)))),
        configureWs);

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

    // ── Connection deadline: exp already elapsed → 4401 without serving requests ────
    [Fact]
    public async Task ExpiredExpClaim_ClosesPromptly_With4401()
    {
        await using var host = await Host();
        var pastExp = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        var path = $"/documents/ws?access_token={Uri.EscapeDataString($"exp:{pastExp}")}";
        await using var ws = await WsTestClient.ConnectAsync(host.Server, path);
        await ws.WaitForCloseAsync();
        Assert.Equal((WebSocketCloseStatus)4401, ws.CloseStatus);
    }

    // ── Connection deadline: MaxConnectionLifetime elapses → 4401; subscription torn down ──
    [Fact]
    public async Task MaxConnectionLifetime_Elapses_ClosesAndTearsDownSubscription()
    {
        await using var host = await AllowAllHost(o => o.MaxConnectionLifetime = TimeSpan.FromMilliseconds(600));
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server);

        var sub = await ws.RequestAsync(new JsonObject
        {
            ["type"] = "listen",
            ["query"] = new JsonObject { ["collection"] = "wsdeadline" },
        });
        var subId = (string)sub["result"]!["subscriptionId"]!;
        await ws.WaitForAsync(f => (string?)f["type"] == "listen.snapshot" && (string?)f["subscriptionId"] == subId);

        await ws.WaitForCloseAsync(5000);
        Assert.Equal((WebSocketCloseStatus)4401, ws.CloseStatus);

        // Behavioral verification the old subscription/scope was torn down: a fresh connection
        // can still listen and receive deltas on the same collection without interference.
        await using var ws2 = await WsTestClient.ConnectAndWelcomeAsync(host.Server);
        var sub2 = await ws2.RequestAsync(new JsonObject
        {
            ["type"] = "listen",
            ["query"] = new JsonObject { ["collection"] = "wsdeadline" },
        });
        var subId2 = (string)sub2["result"]!["subscriptionId"]!;
        await ws2.WaitForAsync(f => (string?)f["type"] == "listen.snapshot" && (string?)f["subscriptionId"] == subId2);

        await ws2.RequestAsync(new JsonObject
        {
            ["type"] = "write",
            ["writes"] = new JsonArray(new JsonObject
            {
                ["set"] = new JsonObject
                {
                    ["path"] = "wsdeadline/x",
                    ["fields"] = new JsonObject { ["n"] = new JsonObject { ["integerValue"] = "1" } },
                },
            }),
        });
        var delta = await ws2.WaitForAsync(f => (string?)f["type"] == "listen.delta" && (string?)f["subscriptionId"] == subId2);
        Assert.Single(delta["changes"]!.AsArray());
    }

    // ── Connection deadline: no exp claim, no MaxConnectionLifetime → no regression ──
    [Fact]
    public async Task NoExpClaim_NoMaxLifetime_ConnectionNotClosed()
    {
        await using var host = await Host();
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server);

        await Task.Delay(300);
        var pong = await ws.RequestAsync(new JsonObject { ["type"] = "ping" });
        Assert.Equal("response", (string?)pong["type"]);
        Assert.Null(ws.CloseStatus);
    }

    // ── Connection deadline: far-future exp must not throw at upgrade (clamping) ────
    [Fact]
    public async Task FarFutureExpClaim_DoesNotThrowAtUpgrade_ConnectionWorksNormally()
    {
        await using var host = await Host();
        var farFuture = DateTimeOffset.UtcNow.AddYears(200).ToUnixTimeSeconds();
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server, $"exp:{farFuture}");

        var pong = await ws.RequestAsync(new JsonObject { ["type"] = "ping" });
        Assert.Equal("response", (string?)pong["type"]);
        Assert.Null(ws.CloseStatus);
    }

    // ── Connection deadline: CloseOnTokenExpiry = false ignores a past exp ──────────
    [Fact]
    public async Task CloseOnTokenExpiryFalse_IgnoresPastExpClaim()
    {
        await using var host = await WsTestHost.StartAsync(Fx.ConnectionString,
            configureWs: o => o.CloseOnTokenExpiry = false);
        var pastExp = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server, $"exp:{pastExp}");

        var pong = await ws.RequestAsync(new JsonObject { ["type"] = "ping" });
        Assert.Equal("response", (string?)pong["type"]);
        Assert.Null(ws.CloseStatus);
    }
}
