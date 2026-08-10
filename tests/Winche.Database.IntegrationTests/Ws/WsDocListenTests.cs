using System.Text.Json.Nodes;
using Winche.Rules;
using Winche.Rules.Expressions;

namespace Winche.Database.IntegrationTests.Ws;

[Collection("postgres")]
public class WsDocListenTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private Task<WsTestHost> AllowAllHost() => WsTestHost.StartAsync(Fx.ConnectionString,
        c => c.UseRules(r => r.Match("{document=**}", b => b.Allow(RuleOperations.All, Expr.Const(true)))));

    [Fact]
    public async Task DocListen_InitialSnapshot_ThenDeltaOnWrite()
    {
        await using var host = await AllowAllHost();
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server);

        var resp = await ws.RequestAsync(new JsonObject { ["type"] = "doc.listen", ["path"] = "wsdl/a", ["protocol"] = 3 });
        Assert.Equal("response", (string?)resp["type"]);
        var subId = (string?)resp["result"]!["subscriptionId"];
        Assert.NotNull(subId);

        // initial snapshot: document absent → empty documents array
        var snap = await ws.WaitForAsync(f =>
            (string?)f["type"] == "listen.snapshot" && (string?)f["subscriptionId"] == subId);
        Assert.Empty(snap["documents"]!.AsArray());

        // create the document → delta frame with exactly one change
        await ws.RequestAsync(new JsonObject
        {
            ["type"] = "write",
            ["writes"] = new JsonArray(new JsonObject
            {
                ["set"] = new JsonObject
                {
                    ["path"] = "wsdl/a",
                    ["fields"] = new JsonObject { ["n"] = new JsonObject { ["integerValue"] = "1" } },
                },
            }),
        });

        var delta = await ws.WaitForAsync(f =>
            (string?)f["type"] == "listen.delta" && (string?)f["subscriptionId"] == subId);
        Assert.Single(delta["changes"]!.AsArray());
    }

    [Fact]
    public async Task DocListen_InvalidPath_ReturnsInvalidArgument()
    {
        await using var host = await AllowAllHost();
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server);

        // "not-a-doc-path" has no slash → not a valid document path.
        var resp = await ws.RequestAsync(new JsonObject { ["type"] = "doc.listen", ["path"] = "not-a-doc-path", ["protocol"] = 3 });
        Assert.Equal("error", (string?)resp["type"]);
        Assert.Equal("INVALID_ARGUMENT", (string?)resp["status"]);
    }

    [Fact]
    public async Task DocListen_WithoutGetRule_PermissionDenied()
    {
        await using var host = await WsTestHost.StartAsync(Fx.ConnectionString); // deny-all (no rules)
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server);

        var resp = await ws.RequestAsync(new JsonObject { ["type"] = "doc.listen", ["path"] = "wsdl/a", ["protocol"] = 3 });
        Assert.Equal("PERMISSION_DENIED", (string?)resp["status"]);
    }

    [Fact]
    public async Task DocListen_OwnerOnlyRule_AllowedForOwner()
    {
        await using var host = await WsTestHost.StartAsync(Fx.ConnectionString,
            c => c.UseRules(r => r.Match("userData/{userId}",
                b => b.Allow(RuleOperations.Of(RuleOperation.Get), Expr.Auth("uid").Eq(Expr.Param("userId"))))));
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server, "uid:alice");

        var resp = await ws.RequestAsync(new JsonObject { ["type"] = "doc.listen", ["path"] = "userData/alice", ["protocol"] = 3 });
        Assert.Equal("response", (string?)resp["type"]);
        Assert.NotNull((string?)resp["result"]!["subscriptionId"]);
    }

    [Fact]
    public async Task DocListen_ResumeWithCurrentToken_EmitsListenCurrent()
    {
        await using var host = await AllowAllHost();
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server);

        await ws.RequestAsync(new JsonObject
        {
            ["type"] = "write",
            ["writes"] = new JsonArray(new JsonObject
            {
                ["set"] = new JsonObject
                {
                    ["path"] = "wsdcur/a",
                    ["fields"] = new JsonObject { ["n"] = new JsonObject { ["integerValue"] = "1" } },
                },
            }),
        });

        var resp = await ws.RequestAsync(new JsonObject { ["type"] = "doc.listen", ["path"] = "wsdcur/a", ["protocol"] = 3 });
        var sub1 = (string?)resp["result"]!["subscriptionId"];
        var snap = await ws.WaitForAsync(f =>
            (string?)f["type"] == "listen.snapshot" && (string?)f["subscriptionId"] == sub1);
        var token = (string)snap["resumeToken"]!;

        await ws.RequestAsync(new JsonObject { ["type"] = "unlisten", ["subscriptionId"] = sub1 });

        var resp2 = await ws.RequestAsync(new JsonObject
        {
            ["type"] = "doc.listen", ["path"] = "wsdcur/a", ["resumeToken"] = token, ["protocol"] = 3,
        });
        var sub2 = (string?)resp2["result"]!["subscriptionId"];

        var current = await ws.WaitForAsync(f =>
            (string?)f["type"] == "listen.current" && (string?)f["subscriptionId"] == sub2);
        Assert.Equal(token, (string)current["resumeToken"]!);
    }

    [Fact]
    public async Task DocListen_Delete_WithProtocol3_EmitsDeletedKind()
    {
        await using var host = await AllowAllHost();
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server);

        await ws.RequestAsync(new JsonObject
        {
            ["type"] = "write",
            ["writes"] = new JsonArray(new JsonObject
            {
                ["set"] = new JsonObject
                {
                    ["path"] = "wsdel/a",
                    ["fields"] = new JsonObject { ["n"] = new JsonObject { ["integerValue"] = "1" } },
                },
            }),
        });

        var resp = await ws.RequestAsync(new JsonObject
        {
            ["type"] = "doc.listen", ["path"] = "wsdel/a", ["protocol"] = 3,
        });
        var subId = (string?)resp["result"]!["subscriptionId"];
        await ws.WaitForAsync(f => (string?)f["type"] == "listen.snapshot" && (string?)f["subscriptionId"] == subId);

        await ws.RequestAsync(new JsonObject
        {
            ["type"] = "write",
            ["writes"] = new JsonArray(new JsonObject
            {
                ["delete"] = new JsonObject { ["path"] = "wsdel/a" },
            }),
        });

        var delta = await ws.WaitForAsync(f =>
            (string?)f["type"] == "listen.delta" && (string?)f["subscriptionId"] == subId);
        var change = delta["changes"]!.AsArray()[0]!.AsObject();
        Assert.Equal("deleted", (string?)change["kind"]);
    }

    // ── Protocol gate: resumeToken became an opaque string at protocol 3, so doc.listen rejects
    // anything below it up front rather than sending frames an old client can't parse ──
    [Fact]
    public async Task DocListen_Protocol2_RejectedWithoutClosingConnection()
    {
        await using var host = await AllowAllHost();
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server);

        var rejected = await ws.RequestAsync(new JsonObject
        {
            ["type"] = "doc.listen", ["path"] = "wsdelprotogate/a", ["protocol"] = 2,
        });
        Assert.Equal(("error", "INVALID_ARGUMENT"), ((string?)rejected["type"], (string?)rejected["status"]));

        // rejection must not kill the socket
        var ping = await ws.RequestAsync(new JsonObject { ["type"] = "ping" });
        Assert.Equal("response", (string?)ping["type"]);
    }
}
