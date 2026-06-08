using System.Text.Json.Nodes;
using Winche.Database.Abstraction;
using Winche.Database.Documents;
using Winche.Sentinel.Models;

namespace Winche.Database.IntegrationTests.Ws;

/// <summary>
/// Read allowed only when claims uid == the document's "owner" field (or doc has no owner).
/// Adapted from samples/Winche.Database.Sample/Configurations/OwnerReadRule.cs — uses
/// GetResourceAsync (the real Sentinel API) rather than a non-existent .Resource property.
/// </summary>
internal sealed class OwnerOnlyReadRule : DocumentAccessRule
{
    public override string Path => "wsl/{docId}";
    public override IReadOnlySet<AccessOperation> Operations => new HashSet<AccessOperation> { AccessOperation.Read };

    public override async Task<bool> EvaluateAsync(AccessContext<Document> context, CancellationToken ct)
    {
        var uid = context.Claims.TryGetValue("uid", out var v) ? v as string : null;
        var doc = await context.GetResourceAsync(ct);
        var owner = doc?.Fields.TryGetValue("owner", out var o) == true
            ? (o as Winche.Database.Values.StringValue)?.Value : null;
        return owner is null || owner == uid;
    }
}

/// <summary>Allows all operations (write, delete, read, aggregate) — used by plain-host tests that don't test access control.</summary>
internal sealed class AllowAllWritesRule : DocumentAccessRule
{
    public override string Path => "**";
    public override IReadOnlySet<AccessOperation> Operations =>
        new HashSet<AccessOperation> { AccessOperation.Write, AccessOperation.Delete, AccessOperation.Read, AccessOperation.Aggregate };

    public override Task<bool> EvaluateAsync(AccessContext<Document> context, CancellationToken ct) =>
        Task.FromResult(true);
}

/// <summary>Allows writes and deletes but NOT reads — pair with owner-scoped read rules.</summary>
internal sealed class AllowWriteDeleteRule : DocumentAccessRule
{
    public override string Path => "**";
    public override IReadOnlySet<AccessOperation> Operations =>
        new HashSet<AccessOperation> { AccessOperation.Write, AccessOperation.Delete };

    public override Task<bool> EvaluateAsync(AccessContext<Document> context, CancellationToken ct) =>
        Task.FromResult(true);
}

[Collection("postgres")]
public class WsListenerTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private Task<WsTestHost> PlainHost() => WsTestHost.StartAsync(Fx.ConnectionString,
        c => c.AddDocumentAccessRule<AllowAllWritesRule>());

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
        await using var ws = await WsTestClient.ConnectV3Async(host.Server);
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
        await using var ws = await WsTestClient.ConnectV3Async(host.Server);
        var sub = await ws.RequestAsync(Listen("wsu"));
        var subId = (string)sub["result"]!["subscriptionId"]!;
        await ws.WaitForAsync(f => (string?)f["type"] == "listen.snapshot");

        await ws.RequestAsync(new JsonObject { ["type"] = "unlisten", ["subscriptionId"] = subId });
        await ws.RequestAsync(Set("wsu/x", ("n", Int(1))));
        await ws.AssertSilenceAsync(f => (string?)f["type"] == "listen.delta");
    }

    [Fact]
    public async Task Resume_Current_IsSilent_ThenResetOnNextChange()
    {
        await using var host = await PlainHost();
        await using var ws = await WsTestClient.ConnectV3Async(host.Server);
        await ws.RequestAsync(Set("wsr/a", ("n", Int(1))));

        var sub1 = await ws.RequestAsync(Listen("wsr"));
        var snap = await ws.WaitForAsync(f => (string?)f["type"] == "listen.snapshot");
        var token = (long)snap["resumeToken"]!;
        await ws.RequestAsync(new JsonObject
            { ["type"] = "unlisten", ["subscriptionId"] = (string)sub1["result"]!["subscriptionId"]! });

        // resume-current: silent until a change; the first frame after is a RESET snapshot
        var sub2 = await ws.RequestAsync(Listen("wsr", token));
        var subId2 = (string)sub2["result"]!["subscriptionId"]!;
        await ws.AssertSilenceAsync(f => (string?)f["subscriptionId"] == subId2);

        await ws.RequestAsync(Set("wsr/b", ("n", Int(2))));
        var reset = await ws.WaitForAsync(f => (string?)f["subscriptionId"] == subId2);
        Assert.Equal("listen.snapshot", (string?)reset["type"]);
        Assert.Equal(2, reset["documents"]!.AsArray().Count);
    }

    // ── I3.5: abrupt disconnect disposes subscriptions (behavioral) ──────────
    [Fact]
    public async Task Disconnect_DisposesSubscriptions()
    {
        await using var host = await PlainHost();

        // First client: subscribe then abruptly disconnect
        var ws1 = await WsTestClient.ConnectV3Async(host.Server);
        var sub = await ws1.RequestAsync(Listen("wsdispose"));
        await ws1.WaitForAsync(f => (string?)f["type"] == "listen.snapshot");
        await ws1.DisposeAsync();   // abrupt disconnect

        await Task.Delay(500);      // allow server to process disconnect and dispose scope

        // Behavioral verification: a second client can still listen and receive data
        await using var ws2 = await WsTestClient.ConnectV3Async(host.Server);
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

    [Fact]
    public async Task ClaimsIsolation_TwoConnections_SameQuery_DifferentVisibility()
    {
        await using var host = await WsTestHost.StartAsync(Fx.ConnectionString, c =>
        {
            c.AddDocumentAccessRule<AllowWriteDeleteRule>();   // writes/deletes allowed for all
            c.AddDocumentAccessRule<OwnerOnlyReadRule>();      // reads filtered by owner
        });
        await using var alice = await WsTestClient.ConnectV3Async(host.Server, "uid:alice");
        await using var bob = await WsTestClient.ConnectV3Async(host.Server, "uid:bob");

        var aliceSub = await alice.RequestAsync(Listen("wsl"));
        var bobSub = await bob.RequestAsync(Listen("wsl"));
        await alice.WaitForAsync(f => (string?)f["type"] == "listen.snapshot");
        await bob.WaitForAsync(f => (string?)f["type"] == "listen.snapshot");

        // alice writes a doc OWNED BY ALICE — bob must not see it
        await alice.RequestAsync(Set("wsl/d1", ("owner", Str("alice")), ("n", Int(1))));

        var aliceDelta = await alice.WaitForAsync(f => (string?)f["type"] == "listen.delta"
            && (string?)f["subscriptionId"] == (string?)aliceSub["result"]!["subscriptionId"]);
        Assert.Equal("added", (string?)aliceDelta["changes"]![0]!["kind"]);

        await bob.AssertSilenceAsync(f => (string?)f["type"] == "listen.delta"
            && (string?)f["subscriptionId"] == (string?)bobSub["result"]!["subscriptionId"]);

        // a doc with no owner is visible to both
        await alice.RequestAsync(Set("wsl/d2", ("n", Int(2))));
        await alice.WaitForAsync(f => (string?)f["type"] == "listen.delta");
        var bobDelta = await bob.WaitForAsync(f => (string?)f["type"] == "listen.delta");
        Assert.Single(bobDelta["changes"]!.AsArray());
        Assert.Equal(1, (int)bobDelta["count"]!);                          // bob sees ONLY d2
    }
}
