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

        var resp = await ws.RequestAsync(new JsonObject { ["type"] = "doc.listen", ["path"] = "wsdl/a" });
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
        var resp = await ws.RequestAsync(new JsonObject { ["type"] = "doc.listen", ["path"] = "not-a-doc-path" });
        Assert.Equal("error", (string?)resp["type"]);
        Assert.Equal("INVALID_ARGUMENT", (string?)resp["status"]);
    }

    [Fact]
    public async Task DocListen_WithoutGetRule_PermissionDenied()
    {
        await using var host = await WsTestHost.StartAsync(Fx.ConnectionString); // deny-all (no rules)
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server);

        var resp = await ws.RequestAsync(new JsonObject { ["type"] = "doc.listen", ["path"] = "wsdl/a" });
        Assert.Equal("PERMISSION_DENIED", (string?)resp["status"]);
    }

    [Fact]
    public async Task DocListen_OwnerOnlyRule_AllowedForOwner()
    {
        await using var host = await WsTestHost.StartAsync(Fx.ConnectionString,
            c => c.UseRules(r => r.Match("userData/{userId}",
                b => b.Allow(RuleOperations.Of(RuleOperation.Get), Expr.Auth("uid").Eq(Expr.Param("userId"))))));
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server, "uid:alice");

        var resp = await ws.RequestAsync(new JsonObject { ["type"] = "doc.listen", ["path"] = "userData/alice" });
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

        var resp = await ws.RequestAsync(new JsonObject { ["type"] = "doc.listen", ["path"] = "wsdcur/a" });
        var sub1 = (string?)resp["result"]!["subscriptionId"];
        var snap = await ws.WaitForAsync(f =>
            (string?)f["type"] == "listen.snapshot" && (string?)f["subscriptionId"] == sub1);
        var token = (long)snap["resumeToken"]!;

        await ws.RequestAsync(new JsonObject { ["type"] = "unlisten", ["subscriptionId"] = sub1 });

        var resp2 = await ws.RequestAsync(new JsonObject
        {
            ["type"] = "doc.listen", ["path"] = "wsdcur/a", ["resumeToken"] = token,
        });
        var sub2 = (string?)resp2["result"]!["subscriptionId"];

        var current = await ws.WaitForAsync(f =>
            (string?)f["type"] == "listen.current" && (string?)f["subscriptionId"] == sub2);
        Assert.Equal(token, (long)current["resumeToken"]!);
    }

    [Fact]
    public async Task DocListen_Delete_WithProtocol2_EmitsDeletedKind()
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
            ["type"] = "doc.listen", ["path"] = "wsdel/a", ["protocol"] = 2,
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

    [Fact]
    public async Task DocListen_Delete_WithoutProtocol_EmitsRemovedKind()
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
                    ["path"] = "wsdel/b",
                    ["fields"] = new JsonObject { ["n"] = new JsonObject { ["integerValue"] = "1" } },
                },
            }),
        });

        var resp = await ws.RequestAsync(new JsonObject { ["type"] = "doc.listen", ["path"] = "wsdel/b" });
        var subId = (string?)resp["result"]!["subscriptionId"];
        await ws.WaitForAsync(f => (string?)f["type"] == "listen.snapshot" && (string?)f["subscriptionId"] == subId);

        await ws.RequestAsync(new JsonObject
        {
            ["type"] = "write",
            ["writes"] = new JsonArray(new JsonObject { ["delete"] = new JsonObject { ["path"] = "wsdel/b" } }),
        });

        var delta = await ws.WaitForAsync(f =>
            (string?)f["type"] == "listen.delta" && (string?)f["subscriptionId"] == subId);
        var change = delta["changes"]!.AsArray()[0]!.AsObject();
        Assert.Equal("removed", (string?)change["kind"]);
    }
}
