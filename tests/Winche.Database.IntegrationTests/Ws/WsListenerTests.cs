using System.Text.Json.Nodes;
using Winche.Rules;
using Winche.Rules.Expressions;

namespace Winche.Database.IntegrationTests.Ws;

[Collection("postgres")]
public class WsListenerTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private Task<WsTestHost> PlainHost() => WsTestHost.StartAsync(Fx.ConnectionString,
        c => c.UseRules(r => r.Match("{document=**}", b => b.Allow(RuleOperations.All, Expr.Const(true)))));

    private static JsonObject Set(string path, params (string K, JsonObject V)[] fields)
    {
        var f = new JsonObject();
        foreach (var (k, v) in fields) f[k] = v;
        return new JsonObject
        {
            ["type"] = "write",
            ["writes"] = new JsonArray(new JsonObject
                { ["set"] = new JsonObject { ["path"] = path, ["fields"] = f } }),
        };
    }

    private static JsonObject Set(string path, long n) => new()
    {
        ["type"] = "write",
        ["writes"] = new JsonArray(new JsonObject
        {
            ["set"] = new JsonObject
            {
                ["path"] = path,
                ["fields"] = new JsonObject { ["n"] = new JsonObject { ["integerValue"] = n.ToString() } },
            },
        }),
    };

    private static JsonObject Delete(string path) => new()
    {
        ["type"] = "write",
        ["writes"] = new JsonArray(new JsonObject { ["delete"] = new JsonObject { ["path"] = path } }),
    };

    private Task<WsTestHost> AllowAllHost() => WsTestHost.StartAsync(Fx.ConnectionString,
        c => c.UseRules(r => r.Match("{document=**}", b => b.Allow(RuleOperations.All, Expr.Const(true)))));

    private static JsonObject Int(long n) => new() { ["integerValue"] = n.ToString() };
    private static JsonObject Str(string s) => new() { ["stringValue"] = s };

    private static JsonObject Listen(string collection, long? resume = null)
    {
        var msg = new JsonObject { ["type"] = "listen", ["query"] = new JsonObject { ["collection"] = collection } };
        if (resume is { } r) msg["resumeToken"] = r;
        return msg;
    }

    [Fact]
    public async Task Listen_InitialSnapshot_ThenDeltas_WithIndicesAndCount()
    {
        await using var host = await PlainHost();
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server);
        await ws.RequestAsync(Set("wsd/a", ("n", Int(1))));

        var sub = await ws.RequestAsync(Listen("wsd"));
        var subId = (string)sub["result"]!["subscriptionId"]!;

        var initial = await ws.WaitForAsync(f => (string?)f["type"] == "listen.snapshot"
                                              && (string?)f["subscriptionId"] == subId);
        Assert.Single(initial["documents"]!.AsArray());
        Assert.True((long)initial["resumeToken"]! > 0);

        await ws.RequestAsync(Set("wsd/b", ("n", Int(2))));
        var delta = await ws.WaitForAsync(f => (string?)f["type"] == "listen.delta");
        var change = delta["changes"]!.AsArray().Single()!;
        Assert.Equal(("added", -1, 1), ((string?)change["kind"], (int)change["oldIndex"]!, (int)change["newIndex"]!));
        Assert.Equal(2, (int)delta["count"]!);

        await ws.RequestAsync(new JsonObject
        {
            ["type"] = "write",
            ["writes"] = new JsonArray(new JsonObject
                { ["delete"] = new JsonObject { ["path"] = "wsd/a" } }),
        });
        var removal = await ws.WaitForAsync(f => (string?)f["type"] == "listen.delta");
        var removed = removal["changes"]!.AsArray().Single()!;
        Assert.Equal(("removed", 0, -1), ((string?)removed["kind"], (int)removed["oldIndex"]!, (int)removed["newIndex"]!));
        Assert.Equal(1, (int)removal["count"]!);
    }

    [Fact]
    public async Task Unlisten_StopsEvents()
    {
        await using var host = await PlainHost();
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server);
        var sub = await ws.RequestAsync(Listen("wsu"));
        var subId = (string)sub["result"]!["subscriptionId"]!;
        await ws.WaitForAsync(f => (string?)f["type"] == "listen.snapshot");

        await ws.RequestAsync(new JsonObject { ["type"] = "unlisten", ["subscriptionId"] = subId });
        await ws.RequestAsync(Set("wsu/x", ("n", Int(1))));
        await ws.AssertSilenceAsync(f => (string?)f["type"] == "listen.delta");
    }

    [Fact]
    public async Task Resume_Current_EmitsListenCurrent_ThenResetOnNextChange()
    {
        await using var host = await PlainHost();
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server);
        await ws.RequestAsync(Set("wsr/a", ("n", Int(1))));

        var sub1 = await ws.RequestAsync(Listen("wsr"));
        var snap = await ws.WaitForAsync(f => (string?)f["type"] == "listen.snapshot");
        var token = (long)snap["resumeToken"]!;
        await ws.RequestAsync(new JsonObject
            { ["type"] = "unlisten", ["subscriptionId"] = (string)sub1["result"]!["subscriptionId"]! });

        // resume-current: emits listen.current immediately; the first data frame after a write is a RESET snapshot
        var sub2 = await ws.RequestAsync(Listen("wsr", token));
        var subId2 = (string)sub2["result"]!["subscriptionId"]!;
        var current = await ws.WaitForAsync(f =>
            (string?)f["type"] == "listen.current" && (string?)f["subscriptionId"] == subId2);
        Assert.Equal(token, (long)current["resumeToken"]!);

        await ws.RequestAsync(Set("wsr/b", ("n", Int(2))));
        var reset = await ws.WaitForAsync(f =>
            (string?)f["type"] == "listen.snapshot" && (string?)f["subscriptionId"] == subId2);
        Assert.Equal(2, reset["documents"]!.AsArray().Count);
    }

    // ── I3.5: abrupt disconnect disposes subscriptions (behavioral) ──────────
    [Fact]
    public async Task Disconnect_DisposesSubscriptions()
    {
        await using var host = await PlainHost();

        // First client: subscribe then abruptly disconnect
        var ws1 = await WsTestClient.ConnectAndWelcomeAsync(host.Server);
        var sub = await ws1.RequestAsync(Listen("wsdispose"));
        await ws1.WaitForAsync(f => (string?)f["type"] == "listen.snapshot");
        await ws1.DisposeAsync();   // abrupt disconnect

        await Task.Delay(500);      // allow server to process disconnect and dispose scope

        // Behavioral verification: a second client can still listen and receive data
        await using var ws2 = await WsTestClient.ConnectAndWelcomeAsync(host.Server);
        var sub2 = await ws2.RequestAsync(Listen("wsdispose"));
        var subId2 = (string)sub2["result"]!["subscriptionId"]!;
        await ws2.WaitForAsync(f => (string?)f["type"] == "listen.snapshot"
                                  && (string?)f["subscriptionId"] == subId2);

        // Write via ws2 — delta must arrive proving the listener is active
        await ws2.RequestAsync(Set("wsdispose/x", ("n", Int(42))));
        var delta = await ws2.WaitForAsync(f => (string?)f["type"] == "listen.delta"
                                             && (string?)f["subscriptionId"] == subId2);
        Assert.Single(delta["changes"]!.AsArray());
    }

    /// <summary>
    /// Verifies per-connection query isolation with the Winche.Rules guard.
    /// Rule: allow write for all; allow read only when resource.owner == request.auth.uid.
    /// Each subscriber issues an owner-constrained query — the constrained query is provably
    /// safe (satisfies the rule) so the listen is accepted. Each user only receives deltas
    /// for documents matching THEIR constraint.
    ///
    /// NOTE (Phase 4b): the Winche.Rules guard does NOT post-filter results; it requires
    /// constrained queries that are provably consistent with the ruleset. The former
    /// Sentinel-guard test used an unconstrained listen and relied on per-document post-filtering
    /// of live updates. That behavior does not exist in the rules guard — each subscriber must
    /// issue their own constrained query.
    /// </summary>
    [Fact]
    public async Task ClaimsIsolation_TwoConnections_ConstrainedQueries_DifferentVisibility()
    {
        await using var host = await WsTestHost.StartAsync(Fx.ConnectionString, c =>
        {
            c.UseRules(r =>
            {
                // Allow writes/deletes for any authenticated user on any path
                r.Match("{document=**}", b => b.Allow(RuleOperations.Write, Expr.Const(true)));
                // Allow reads only where owner == request.auth.uid (constrained query required)
                r.Match("wsl/{docId}", b =>
                    b.Allow(RuleOperations.Read,
                        Expr.Resource("owner").Eq(Expr.Auth("uid"))));
            });
        });
        await using var alice = await WsTestClient.ConnectAndWelcomeAsync(host.Server, "uid:alice");
        await using var bob = await WsTestClient.ConnectAndWelcomeAsync(host.Server, "uid:bob");

        // Each user subscribes with a constrained query matching only their own documents.
        // This satisfies the rule (owner == uid) and is therefore accepted by the rules guard.
        var aliceSub = await alice.RequestAsync(new JsonObject
        {
            ["type"] = "listen",
            ["query"] = new JsonObject
            {
                ["collection"] = "wsl",
                ["where"] = new JsonObject
                {
                    ["field"] = "owner", ["op"] = "eq",
                    ["value"] = new JsonObject { ["stringValue"] = "alice" },
                },
            },
        });
        var bobSub = await bob.RequestAsync(new JsonObject
        {
            ["type"] = "listen",
            ["query"] = new JsonObject
            {
                ["collection"] = "wsl",
                ["where"] = new JsonObject
                {
                    ["field"] = "owner", ["op"] = "eq",
                    ["value"] = new JsonObject { ["stringValue"] = "bob" },
                },
            },
        });

        var aliceSubId = (string?)aliceSub["result"]!["subscriptionId"];
        var bobSubId = (string?)bobSub["result"]!["subscriptionId"];
        await alice.WaitForAsync(f => (string?)f["type"] == "listen.snapshot"
                                   && (string?)f["subscriptionId"] == aliceSubId);
        await bob.WaitForAsync(f => (string?)f["type"] == "listen.snapshot"
                                 && (string?)f["subscriptionId"] == bobSubId);

        // alice writes a doc owned by alice — only alice's subscription fires
        await alice.RequestAsync(Set("wsl/d1", ("owner", Str("alice")), ("n", Int(1))));

        var aliceDelta = await alice.WaitForAsync(f => (string?)f["type"] == "listen.delta"
            && (string?)f["subscriptionId"] == aliceSubId);
        Assert.Equal("added", (string?)aliceDelta["changes"]![0]!["kind"]);

        // Bob's constrained subscription (owner == "bob") must NOT receive alice's document
        await bob.AssertSilenceAsync(f => (string?)f["type"] == "listen.delta"
            && (string?)f["subscriptionId"] == bobSubId);

        // bob writes a doc owned by bob — only bob's subscription fires
        await bob.RequestAsync(Set("wsl/d2", ("owner", Str("bob")), ("n", Int(2))));

        var bobDelta = await bob.WaitForAsync(f => (string?)f["type"] == "listen.delta"
            && (string?)f["subscriptionId"] == bobSubId);
        Assert.Equal("added", (string?)bobDelta["changes"]![0]!["kind"]);

        // Alice's constrained subscription (owner == "alice") must NOT receive bob's document
        await alice.AssertSilenceAsync(f => (string?)f["type"] == "listen.delta"
            && (string?)f["subscriptionId"] == aliceSubId);
    }

    [Fact]
    public async Task Listen_Delete_WithProtocol2_EmitsDeletedKind()
    {
        await using var host = await AllowAllHost();
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server);

        await ws.RequestAsync(Set("wsq/a", 1));
        var resp = await ws.RequestAsync(new JsonObject
        {
            ["type"] = "listen",
            ["query"] = new JsonObject { ["collection"] = "wsq" },
            ["protocol"] = 2,
        });
        var subId = (string?)resp["result"]!["subscriptionId"];
        await ws.WaitForAsync(f => (string?)f["type"] == "listen.snapshot" && (string?)f["subscriptionId"] == subId);

        await ws.RequestAsync(Delete("wsq/a"));

        var delta = await ws.WaitForAsync(f =>
            (string?)f["type"] == "listen.delta" && (string?)f["subscriptionId"] == subId);
        var change = delta["changes"]!.AsArray()[0]!.AsObject();
        Assert.Equal("deleted", (string?)change["kind"]);
    }

    [Fact]
    public async Task Listen_Delete_WithoutProtocol_EmitsRemovedKind()
    {
        await using var host = await AllowAllHost();
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server);

        await ws.RequestAsync(Set("wsq2/a", 1));
        var resp = await ws.RequestAsync(new JsonObject
        {
            ["type"] = "listen",
            ["query"] = new JsonObject { ["collection"] = "wsq2" },
        });
        var subId = (string?)resp["result"]!["subscriptionId"];
        await ws.WaitForAsync(f => (string?)f["type"] == "listen.snapshot" && (string?)f["subscriptionId"] == subId);

        await ws.RequestAsync(Delete("wsq2/a"));

        var delta = await ws.WaitForAsync(f =>
            (string?)f["type"] == "listen.delta" && (string?)f["subscriptionId"] == subId);
        var change = delta["changes"]!.AsArray()[0]!.AsObject();
        Assert.Equal("removed", (string?)change["kind"]);
    }

    [Fact]
    public async Task Listen_LimitWindowExit_WithProtocol2_EmitsRemovedNotDeleted()
    {
        await using var host = await AllowAllHost();
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server);

        await ws.RequestAsync(Set("wsq3/a", 1));   // only member of a limit-1 query
        var resp = await ws.RequestAsync(new JsonObject
        {
            ["type"] = "listen",
            ["query"] = new JsonObject
            {
                ["collection"] = "wsq3",
                ["orderBy"] = new JsonArray(new JsonObject { ["field"] = "n", ["direction"] = "asc" }),
                ["limit"] = 1,
            },
            ["protocol"] = 2,
        });
        var subId = (string?)resp["result"]!["subscriptionId"];
        await ws.WaitForAsync(f => (string?)f["type"] == "listen.snapshot" && (string?)f["subscriptionId"] == subId);

        // Insert b with a smaller key → b enters the window, a is pushed out but STILL EXISTS.
        await ws.RequestAsync(Set("wsq3/b", 0));

        var delta = await ws.WaitForAsync(f =>
            (string?)f["type"] == "listen.delta" && (string?)f["subscriptionId"] == subId);
        var removed = delta["changes"]!.AsArray()
            .Select(c => c!.AsObject())
            .First(c => (string?)c["document"]!["id"] == "a");
        Assert.Equal("removed", (string?)removed["kind"]);   // existence-at-diff-time: a still exists
    }

    [Fact]
    public async Task Listen_ResumeWithCurrentToken_EmitsListenCurrent()
    {
        await using var host = await AllowAllHost();
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server);

        await ws.RequestAsync(Set("wscur/a", 1));
        var resp = await ws.RequestAsync(new JsonObject
        {
            ["type"] = "listen",
            ["query"] = new JsonObject { ["collection"] = "wscur" },
        });
        var sub1 = (string?)resp["result"]!["subscriptionId"];
        var snap = await ws.WaitForAsync(f =>
            (string?)f["type"] == "listen.snapshot" && (string?)f["subscriptionId"] == sub1);
        var token = (long)snap["resumeToken"]!;

        await ws.RequestAsync(new JsonObject { ["type"] = "unlisten", ["subscriptionId"] = sub1 });

        // Re-listen with the (still-current) token; nothing changed in between.
        var resp2 = await ws.RequestAsync(new JsonObject
        {
            ["type"] = "listen",
            ["query"] = new JsonObject { ["collection"] = "wscur" },
            ["resumeToken"] = token,
        });
        var sub2 = (string?)resp2["result"]!["subscriptionId"];

        var current = await ws.WaitForAsync(f =>
            (string?)f["type"] == "listen.current" && (string?)f["subscriptionId"] == sub2);
        Assert.Equal(token, (long)current["resumeToken"]!);
    }

    [Fact]
    public async Task Listen_DeleteAmidOtherChangeInOneBatch_ClassifiesDeletedCorrectly()
    {
        await using var host = await AllowAllHost();
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server);

        await ws.RequestAsync(Set("wsq4/a", 1));
        await ws.RequestAsync(Set("wsq4/b", 2));
        var resp = await ws.RequestAsync(new JsonObject
        {
            ["type"] = "listen",
            ["query"] = new JsonObject { ["collection"] = "wsq4" },
            ["protocol"] = 2,
        });
        var subId = (string?)resp["result"]!["subscriptionId"];
        await ws.WaitForAsync(f => (string?)f["type"] == "listen.snapshot" && (string?)f["subscriptionId"] == subId);

        // One atomic batch: delete `a`, modify `b`. Both land in a single delta, so
        // the classification loop must label `a` deleted while `b` stays modified —
        // a deterministic proxy for the coalesced removed-then-deleted case.
        await ws.RequestAsync(new JsonObject
        {
            ["type"] = "write",
            ["writes"] = new JsonArray(
                new JsonObject { ["delete"] = new JsonObject { ["path"] = "wsq4/a" } },
                new JsonObject
                {
                    ["set"] = new JsonObject
                    {
                        ["path"] = "wsq4/b",
                        ["fields"] = new JsonObject { ["n"] = new JsonObject { ["integerValue"] = "9" } },
                    },
                }),
        });

        var delta = await ws.WaitForAsync(f =>
            (string?)f["type"] == "listen.delta" && (string?)f["subscriptionId"] == subId);
        var changes = delta["changes"]!.AsArray().Select(c => c!.AsObject()).ToList();
        var aChange = changes.First(c => (string?)c["document"]!["id"] == "a");
        var bChange = changes.First(c => (string?)c["document"]!["id"] == "b");
        Assert.Equal("deleted", (string?)aChange["kind"]);    // truly deleted
        Assert.Equal("modified", (string?)bChange["kind"]);   // still present, changed
    }
}
