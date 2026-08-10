using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Winche.Database.Authorization;
using Winche.Database.DependencyInjection;
using Winche.Database.Querying.Ast;
using Winche.Database.Runtime;
using Winche.Database.Runtime.ChangeFeed;
using Winche.Database.Runtime.Listening;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;
using Winche.Rules;
using Winche.Rules.Expressions;
using QuerySnapshot = Winche.Database.Runtime.Listening.QuerySnapshot;

namespace Winche.Database.IntegrationTests;

/// <summary>
/// Integration tests for the live-subscription re-authorization fix: <see cref="RuleGuardedDocumentListener"/>
/// and <see cref="RuleGuardedQueryListener"/> re-evaluate rules on every emission that carries a document
/// body, instead of authorizing once at subscribe time and streaming from an unguarded source forever.
/// <para>
/// Rig/helper conventions are lifted directly from <see cref="ListenToDocumentTests"/> (document-listener
/// pump rig), <see cref="ListenerTests"/> (query-listener pump rig), and <see cref="RuleGuardedDatabaseTests"/>
/// (guard construction, <see cref="StaticClaimsAccessor"/>, <see cref="RuleSetBuilder"/> usage).
/// </para>
/// </summary>
[Collection("postgres")]
public class RuleGuardedListenReauthTests(PostgresFixture fx) : QueryTestBase(fx)
{
    // ── Guard construction helpers ───────────────────────────────────────────

    private static Dictionary<string, Value> Fields(params (string K, Value V)[] entries) =>
        entries.ToDictionary(e => e.K, e => e.V);

    private static RuleGuardedDocumentDatabase Guard(DocumentDatabase core, RuleSet ruleset, IReadOnlyDictionary<string, object?>? claims) =>
        new(core, new RuleEngine(new StaticRuleSetRepository(ruleset), WincheRuleValueComparer.Instance), new StaticClaimsAccessor(claims), Options.Create(new WincheDatabaseOptions()));

    /// <summary>
    /// Same as <see cref="Guard"/>, but backed by a <see cref="MutableRuleSetRepository"/> so the test can
    /// hot-swap the active ruleset mid-subscription via the returned repository — mirrors how
    /// <c>WincheDatabaseKeys.RuleEngine</c> exposes <c>IMutableRuleSetRepository</c> for runtime rule swaps
    /// (see README "Hot-swapping rules at runtime" and <c>RuleEngineIsolationTests</c>).
    /// </summary>
    private static (RuleGuardedDocumentDatabase Guard, MutableRuleSetRepository Repo) GuardMutable(
        DocumentDatabase core, RuleSet initial, IReadOnlyDictionary<string, object?>? claims)
    {
        var repo = new MutableRuleSetRepository(initial);
        var guard = new RuleGuardedDocumentDatabase(core, new RuleEngine(repo, WincheRuleValueComparer.Instance), new StaticClaimsAccessor(claims), Options.Create(new WincheDatabaseOptions()));
        return (guard, repo);
    }

    // ── Document-listener rig: core + DocumentListenerRegistry + running pump ──

    private sealed class DocRig : IAsyncDisposable
    {
        public required DocumentDatabase Db { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required Task Run { get; init; }
        public async ValueTask DisposeAsync() { Cts.Cancel(); await Run; }
    }

    private DocRig StartDocRig()
    {
        var opts = Options.Create(new WincheDatabaseOptions());
        var docRegistry = new DocumentListenerRegistry(Fx.DataSource);
        var db = new DocumentDatabase(Fx.DataSource, opts, docListeners: docRegistry);
        var pump = new ChangeFeedPump(Fx.DataSource, [docRegistry],
            new ChangeFeedConfig { PollInterval = TimeSpan.FromMilliseconds(200) },
            NullLogger<ChangeFeedPump>.Instance);
        var cts = new CancellationTokenSource();
        return new DocRig { Db = db, Cts = cts, Run = Task.Run(() => pump.RunAsync(cts.Token)) };
    }

    // ── Query-listener rig: core + QueryListenerRegistry + running pump ────────

    private sealed class QueryRig : IAsyncDisposable
    {
        public required DocumentDatabase Db { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required Task Run { get; init; }
        public async ValueTask DisposeAsync() { Cts.Cancel(); await Run; }
    }

    private QueryRig StartQueryRig()
    {
        var opts = Options.Create(new WincheDatabaseOptions());
        var registry = new QueryListenerRegistry(Fx.DataSource);
        var db = new DocumentDatabase(Fx.DataSource, opts, registry);
        var pump = new ChangeFeedPump(Fx.DataSource, [registry],
            new ChangeFeedConfig { PollInterval = TimeSpan.FromMilliseconds(200) },
            NullLogger<ChangeFeedPump>.Instance);
        var cts = new CancellationTokenSource();
        return new QueryRig { Db = db, Cts = cts, Run = Task.Run(() => pump.RunAsync(cts.Token)) };
    }

    // ── Await helpers (identical pattern to ListenToDocumentTests/ListenerTests) ─

    private static async Task<DocumentSnapshot> NextAsync(IAsyncEnumerator<DocumentSnapshot> e, int timeoutMs = 10000)
    {
        var move = e.MoveNextAsync().AsTask();
        Assert.True(await Task.WhenAny(move, Task.Delay(timeoutMs)) == move, "timed out waiting for snapshot");
        Assert.True(await move, "snapshot stream ended unexpectedly");
        return e.Current;
    }

    private static async Task<QuerySnapshot> NextAsync(IAsyncEnumerator<QuerySnapshot> e, int timeoutMs = 10000)
    {
        var move = e.MoveNextAsync().AsTask();
        Assert.True(await Task.WhenAny(move, Task.Delay(timeoutMs)) == move, "timed out waiting for snapshot");
        Assert.True(await move, "snapshot stream ended unexpectedly");
        return e.Current;
    }

    // ── 1. The core exploit, end to end ────────────────────────────────────────

    /// <summary>
    /// Rule grants get based on document contents (caller's uid in a "readers" array field).
    /// The subscriber is authorized at subscribe time and receives the initial snapshot. The "owner"
    /// then mutates the document — removing the subscriber from "readers" AND changing a "secret" field
    /// in the same write — so the rule no longer allows. The subscription must terminate with
    /// <see cref="AccessDeniedException"/> and the post-revocation "secret" value must never be observed.
    /// </summary>
    [Fact]
    public async Task LiveGet_AccessRevokedMidStream_TerminatesAndNeverDeliversRevokedBody()
    {
        await using var rig = StartDocRig();
        var ruleset = RuleSetBuilder.Build(r =>
            r.Match("live1/{docId}", b =>
                b.Allow(RuleOperations.Of(RuleOperation.Get), Expr.Auth("uid").In(Expr.Resource("readers")))));

        await rig.Db.WriteAsync([new SetWrite
        {
            Path = "live1/shared",
            Fields = Fields(("readers", new ArrayValue([new StringValue("alice")])), ("secret", new StringValue("v1"))),
        }]);

        var guard = Guard(rig.Db, ruleset, new Dictionary<string, object?> { ["uid"] = "alice" });
        await using var listener = await guard.ListenToDocumentAsync("live1/shared");
        await using var e = listener.Snapshots().GetAsyncEnumerator();

        var seen = new List<DocumentSnapshot>();
        var initial = await NextAsync(e);
        seen.Add(initial);
        Assert.True(initial.Exists);
        Assert.Equal("v1", ((StringValue)initial.Document!.Fields["secret"]).Value);

        // The "owner" revokes alice's read access and changes the secret in the same write.
        const string revokedSecret = "v2-must-never-leak";
        await rig.Db.WriteAsync([new SetWrite
        {
            Path = "live1/shared",
            Fields = Fields(("readers", new ArrayValue([new StringValue("bob")])), ("secret", new StringValue(revokedSecret))),
        }]);

        var ex = await Assert.ThrowsAsync<AccessDeniedException>(async () => seen.Add(await NextAsync(e)));
        Assert.Equal("live1/shared", ex.Path);
        Assert.Equal("get", ex.Operation);

        // The revoked body was never delivered to the subscriber.
        Assert.DoesNotContain(seen, s => s.Document is not null &&
            s.Document.Fields.TryGetValue("secret", out var v) && v is StringValue { Value: revokedSecret });
    }

    // ── 2. Stale-provider proof ─────────────────────────────────────────────────

    /// <summary>
    /// Same shape as the core exploit, but the rule uses <c>exists()</c> against a SEPARATE membership
    /// document rather than the watched document's own fields. The membership document is deleted, then
    /// the watched document is mutated (to trigger a fresh re-evaluation). The subscription must terminate
    /// — proving the per-evaluation <c>PostgresDocumentSource</c> is not serving a cached "exists=true"
    /// result from the earlier (subscribe-time) evaluation, when the membership document still existed.
    /// </summary>
    [Fact]
    public async Task LiveGet_MembershipDocDeleted_FreshProviderSeesDeletion_Terminates()
    {
        await using var rig = StartDocRig();
        var ruleset = RuleSetBuilder.Build(r =>
            r.Match("live2/{docId}", b =>
                b.Allow(RuleOperations.Of(RuleOperation.Get), Expr.Exists(Expr.Const("live2meta/team-x")))));

        await rig.Db.WriteAsync([new SetWrite { Path = "live2meta/team-x", Fields = Fields(("active", new BooleanValue(true))) }]);
        await rig.Db.WriteAsync([new SetWrite { Path = "live2/shared", Fields = Fields(("v", new IntegerValue(1))) }]);

        var guard = Guard(rig.Db, ruleset, new Dictionary<string, object?> { ["uid"] = "alice" });
        await using var listener = await guard.ListenToDocumentAsync("live2/shared");
        await using var e = listener.Snapshots().GetAsyncEnumerator();

        var initial = await NextAsync(e);
        Assert.True(initial.Exists);

        // Delete the membership document backing exists(). A stale/cached provider from the earlier
        // evaluation would still report it as existing.
        await rig.Db.WriteAsync([new DeleteWrite { Path = "live2meta/team-x" }]);

        // Touch the watched document itself so the listener performs a fresh re-evaluation.
        await rig.Db.WriteAsync([new SetWrite { Path = "live2/shared", Fields = Fields(("v", new IntegerValue(2))) }]);

        var ex = await Assert.ThrowsAsync<AccessDeniedException>(() => NextAsync(e));
        Assert.Equal("live2/shared", ex.Path);
        Assert.Equal("get", ex.Operation);
    }

    // ── 3. No false positives ────────────────────────────────────────────────────

    /// <summary>A still-authorized subscriber keeps receiving updates normally across several mutations.</summary>
    [Fact]
    public async Task LiveGet_StillAuthorized_KeepsDeliveringAcrossMultipleMutations()
    {
        await using var rig = StartDocRig();
        var ruleset = RuleSetBuilder.Build(r =>
            r.Match("live3/{docId}", b =>
                b.Allow(RuleOperations.Of(RuleOperation.Get), Expr.Auth("uid").In(Expr.Resource("readers")))));

        await rig.Db.WriteAsync([new SetWrite
        {
            Path = "live3/doc",
            Fields = Fields(("readers", new ArrayValue([new StringValue("alice")])), ("n", new IntegerValue(0))),
        }]);

        var guard = Guard(rig.Db, ruleset, new Dictionary<string, object?> { ["uid"] = "alice" });
        await using var listener = await guard.ListenToDocumentAsync("live3/doc");
        await using var e = listener.Snapshots().GetAsyncEnumerator();
        await NextAsync(e);   // initial

        for (var i = 1; i <= 3; i++)
        {
            await rig.Db.WriteAsync([new SetWrite
            {
                Path = "live3/doc",
                Fields = Fields(("readers", new ArrayValue([new StringValue("alice")])), ("n", new IntegerValue(i))),
            }]);
            var snap = await NextAsync(e);
            Assert.True(snap.Exists);
            Assert.Equal(new IntegerValue(i), snap.Document!.Fields["n"]);
        }
    }

    // ── 4. Deletes still delivered ───────────────────────────────────────────────

    /// <summary>
    /// Deleting the watched document does NOT terminate the subscription — the absence carries no
    /// document body, so it passes through unauthorized by design (a resource-dependent rule evaluated
    /// against a null resource would otherwise deny every legitimate delete).
    /// </summary>
    [Fact]
    public async Task LiveGet_DocumentDeleted_DeliversAbsenceWithoutTerminating()
    {
        await using var rig = StartDocRig();
        var ruleset = RuleSetBuilder.Build(r =>
            r.Match("live4/{docId}", b =>
                b.Allow(RuleOperations.Of(RuleOperation.Get), Expr.Auth("uid").In(Expr.Resource("readers")))));

        await rig.Db.WriteAsync([new SetWrite
        {
            Path = "live4/doc",
            Fields = Fields(("readers", new ArrayValue([new StringValue("alice")]))),
        }]);

        var guard = Guard(rig.Db, ruleset, new Dictionary<string, object?> { ["uid"] = "alice" });
        await using var listener = await guard.ListenToDocumentAsync("live4/doc");
        await using var e = listener.Snapshots().GetAsyncEnumerator();
        await NextAsync(e);   // initial

        await rig.Db.WriteAsync([new DeleteWrite { Path = "live4/doc" }]);
        var deleted = await NextAsync(e);
        Assert.False(deleted.Exists);
        Assert.Null(deleted.Document);
    }

    // ── 5. Query listener, tightened ruleset ─────────────────────────────────────

    /// <summary>
    /// A query listener whose ruleset is hot-swapped (via <see cref="IMutableRuleSetRepository"/>, the same
    /// primitive resolved under <c>WincheDatabaseKeys.RuleEngine</c> in production) to something stricter
    /// terminates on its next emission.
    /// </summary>
    [Fact]
    public async Task LiveQuery_RulesetTightenedMidStream_TerminatesOnNextEmission()
    {
        await using var rig = StartQueryRig();
        var ruleset = RuleSetBuilder.Build(r =>
            r.Match("live5/{docId}", b =>
                b.Allow(RuleOperations.Read, Expr.Resource("isPublic").Eq(true))));

        var (guard, repo) = GuardMutable(rig.Db, ruleset, new Dictionary<string, object?> { ["uid"] = "alice" });

        var query = new Query("live5", Where: new FieldFilter(F("isPublic"), FilterOperator.Eq, new BooleanValue(true)));
        await using var listener = guard.Listen(query);
        await using var e = listener.Snapshots().GetAsyncEnumerator();

        var initial = await NextAsync(e);
        Assert.Empty(initial.Documents);

        await rig.Db.WriteAsync([new SetWrite { Path = "live5/a", Fields = Fields(("isPublic", new BooleanValue(true))) }]);
        var snap = await NextAsync(e);
        Assert.Single(snap.Documents);

        // Tighten: hot-swap to an empty ruleset — no rule matches "live5" anymore.
        repo.Update(RuleSetBuilder.Build(_ => { }));

        await rig.Db.WriteAsync([new SetWrite { Path = "live5/b", Fields = Fields(("isPublic", new BooleanValue(true))) }]);
        var ex = await Assert.ThrowsAsync<AccessDeniedException>(() => NextAsync(e));
        Assert.Equal("live5", ex.Path);
        Assert.Equal("list", ex.Operation);
    }

    // ── 6. Query listener, unchanged ruleset ─────────────────────────────────────

    /// <summary>A query listener whose ruleset is never touched keeps delivering normally.</summary>
    [Fact]
    public async Task LiveQuery_RulesetUnchanged_KeepsDeliveringNormally()
    {
        await using var rig = StartQueryRig();
        var ruleset = RuleSetBuilder.Build(r =>
            r.Match("live6/{docId}", b =>
                b.Allow(RuleOperations.Read, Expr.Resource("isPublic").Eq(true))));

        var guard = Guard(rig.Db, ruleset, new Dictionary<string, object?> { ["uid"] = "alice" });

        var query = new Query("live6", Where: new FieldFilter(F("isPublic"), FilterOperator.Eq, new BooleanValue(true)));
        await using var listener = guard.Listen(query);
        await using var e = listener.Snapshots().GetAsyncEnumerator();
        await NextAsync(e);   // initial empty

        for (var i = 0; i < 3; i++)
        {
            await rig.Db.WriteAsync([new SetWrite { Path = $"live6/doc{i}", Fields = Fields(("isPublic", new BooleanValue(true))) }]);
            var snap = await NextAsync(e);
            Assert.Equal(i + 1, snap.Documents.Count);
        }
    }
}
