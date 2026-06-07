using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Winche.Database.Models;
using Winche.Database.Querying.Ast;
using Winche.Database.Runtime;
using Winche.Database.Runtime.ChangeFeed;
using Winche.Database.Runtime.Listening;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;
using QuerySnapshot = Winche.Database.Runtime.Listening.QuerySnapshot;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class ListenerTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private static Dictionary<string, Value> Map(params (string K, Value V)[] e) => e.ToDictionary(x => x.K, x => x.V);

    private sealed class Rig : IAsyncDisposable
    {
        public required DocumentDatabase Db { get; init; }
        public required ListenerRegistry Registry { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required Task Run { get; init; }
        public async ValueTask DisposeAsync() { Cts.Cancel(); await Run; }
    }

    private Rig Start()
    {
        var opts = Options.Create(new StoreOptions { TableName = Fx.Table });
        var registry = new ListenerRegistry(Fx.DataSource, opts);
        var db = new DocumentDatabase(Fx.DataSource, opts, registry);
        var pump = new ChangeFeedPump(Fx.DataSource, Fx.Table, [registry],
            new ChangeFeedConfig { PollInterval = TimeSpan.FromMilliseconds(200) },
            NullLogger<ChangeFeedPump>.Instance);
        var cts = new CancellationTokenSource();
        return new Rig { Db = db, Registry = registry, Cts = cts, Run = Task.Run(() => pump.RunAsync(cts.Token)) };
    }

    private static async Task<QuerySnapshot> NextAsync(IAsyncEnumerator<QuerySnapshot> e, int timeoutMs = 10000)
    {
        var move = e.MoveNextAsync().AsTask();
        Assert.True(await Task.WhenAny(move, Task.Delay(timeoutMs)) == move, "timed out waiting for snapshot");
        Assert.True(await move, "snapshot stream ended unexpectedly");
        return e.Current;
    }

    [Fact]
    public async Task Listener_InitialThenAddModifyRemove_WithIndices()
    {
        await using var rig = Start();
        await rig.Db.WriteAsync([new SetWrite { Path = "c/a", Fields = Map(("n", new IntegerValue(1))) }]);
        await Task.Delay(400);                                            // pump cursor past seed

        var query = new QueryAst("c", OrderBy: [new OrderAst(F("n"))]);
        await using var listener = rig.Db.Listen(query);
        await using var e = listener.Snapshots().GetAsyncEnumerator();

        var initial = await NextAsync(e);
        Assert.Equal("a", Assert.Single(initial.Documents).Id);
        Assert.Equal(ListenChangeType.Added, Assert.Single(initial.Changes).Type);

        // ADD: b with n=0 → sorts FIRST
        await rig.Db.WriteAsync([new SetWrite { Path = "c/b", Fields = Map(("n", new IntegerValue(0))) }]);
        var afterAdd = await NextAsync(e);
        Assert.Equal(["b", "a"], afterAdd.Documents.Select(d => d.Id));
        var add = Assert.Single(afterAdd.Changes);
        Assert.Equal((ListenChangeType.Added, -1, 0), (add.Type, add.OldIndex, add.NewIndex));

        // MODIFY: a's n drops to -1 → moves to front
        await rig.Db.WriteAsync([new SetWrite { Path = "c/a", Fields = Map(("n", new IntegerValue(-1))) }]);
        var afterMod = await NextAsync(e);
        Assert.Equal(["a", "b"], afterMod.Documents.Select(d => d.Id));
        var mod = Assert.Single(afterMod.Changes);
        Assert.Equal((ListenChangeType.Modified, 1, 0), (mod.Type, mod.OldIndex, mod.NewIndex));

        // REMOVE
        await rig.Db.WriteAsync([new DeleteWrite { Path = "c/b" }]);
        var afterRemove = await NextAsync(e);
        Assert.Equal(["a"], afterRemove.Documents.Select(d => d.Id));
        var rem = Assert.Single(afterRemove.Changes);
        Assert.Equal((ListenChangeType.Removed, 1, -1), (rem.Type, rem.OldIndex, rem.NewIndex));
        Assert.Equal("b", rem.Document.Id);
    }

    [Fact]
    public async Task IrrelevantChange_NoEmission_FilteredQueriesShortCircuit()
    {
        await using var rig = Start();
        var query = new QueryAst("c", Where: new FieldFilterAst(F("hot"), FilterOperator.Eq, new BooleanValue(true)));
        await using var listener = rig.Db.Listen(query);
        await using var e = listener.Snapshots().GetAsyncEnumerator();
        await NextAsync(e);                                               // empty initial

        await rig.Db.WriteAsync([new SetWrite { Path = "c/cold", Fields = Map(("hot", new BooleanValue(false))) }]);
        await rig.Db.WriteAsync([new SetWrite { Path = "other/x", Fields = Map() }]);  // different collection

        var next = e.MoveNextAsync().AsTask();
        Assert.NotSame(next, await Task.WhenAny(next, Task.Delay(1500)));  // nothing arrives

        await rig.Db.WriteAsync([new SetWrite { Path = "c/hot1", Fields = Map(("hot", new BooleanValue(true))) }]);
        Assert.True(await next);                                          // the pending MoveNext completes now
        Assert.Equal("hot1", Assert.Single(e.Current.Documents).Id);
    }

    [Fact]
    public async Task SharedGroup_BothListenersGetSnapshots()
    {
        await using var rig = Start();
        var query = new QueryAst("c");
        await using var l1 = rig.Db.Listen(query);
        await using var l2 = rig.Db.Listen(query);
        await using var e1 = l1.Snapshots().GetAsyncEnumerator();
        await using var e2 = l2.Snapshots().GetAsyncEnumerator();
        await NextAsync(e1);
        await NextAsync(e2);
        Assert.Equal(1, rig.Registry.GroupCount);                          // shared

        await rig.Db.WriteAsync([new SetWrite { Path = "c/x", Fields = Map() }]);
        Assert.Single((await NextAsync(e1)).Documents);
        Assert.Single((await NextAsync(e2)).Documents);
    }

    [Fact]
    public async Task Dispose_Unsubscribes_GroupDiesWithLastListener()
    {
        await using var rig = Start();
        var listener = rig.Db.Listen(new QueryAst("c"));
        await using (var e = listener.Snapshots().GetAsyncEnumerator())
            await NextAsync(e);
        Assert.Equal(1, rig.Registry.GroupCount);
        await listener.DisposeAsync();
        Assert.Equal(0, rig.Registry.GroupCount);
    }

    [Fact]
    public async Task Resume_NoRelevantChanges_SuppressesInitialSnapshot()
    {
        await using var rig = Start();
        await rig.Db.WriteAsync([new SetWrite { Path = "c/a", Fields = Map() }]);

        // first listener captures a resume token
        long token;
        await using (var l = rig.Db.Listen(new QueryAst("c")))
        await using (var e = l.Snapshots().GetAsyncEnumerator())
            token = (await NextAsync(e)).ResumeToken;

        // resume with no changes since → silence until a real change
        await using var resumed = rig.Db.Listen(new QueryAst("c"), new ListenOptions(ResumeFrom: token));
        await using var er = resumed.Snapshots().GetAsyncEnumerator();
        var pending = er.MoveNextAsync().AsTask();
        Assert.NotSame(pending, await Task.WhenAny(pending, Task.Delay(1500)));

        await rig.Db.WriteAsync([new SetWrite { Path = "c/b", Fields = Map() }]);
        Assert.True(await pending);
        Assert.Equal(2, er.Current.Documents.Count);                       // fresh full snapshot
    }

    [Fact]
    public async Task GuardedListener_FiltersAndReDiffs()
    {
        await using var rig = Start();
        var rules = new DenyPrefixEvaluator("c/secret");
        var guarded = new GuardedDocumentDatabase(rig.Db, rules);

        await using var listener = guarded.Listen(new QueryAst("c"));
        await using var e = listener.Snapshots().GetAsyncEnumerator();
        await NextAsync(e);                                                // empty initial

        await rig.Db.WriteAsync(
        [
            new SetWrite { Path = "c/secret1", Fields = Map() },           // wait: prefix is c/secret
            new SetWrite { Path = "c/visible", Fields = Map() },
        ]);

        var snap = await NextAsync(e);
        Assert.Equal("visible", Assert.Single(snap.Documents).Id);         // secret filtered out
        var add = Assert.Single(snap.Changes);
        Assert.Equal((ListenChangeType.Added, 0), (add.Type, add.NewIndex)); // re-diffed index, not the core's
    }

    // ── Tests: I1 resume paths ───────────────────────────────────────────────

    [Fact]
    public async Task Resume_WithChanges_InitialSnapshotArrives()
    {
        await using var rig = Start();
        await rig.Db.WriteAsync([new SetWrite { Path = "c/a", Fields = Map() }]);

        // capture a resume token; dispose the listener (completing the channel) BEFORE the enumerator
        long token;
        var l0 = rig.Db.Listen(new QueryAst("c"));
        var e0 = l0.Snapshots().GetAsyncEnumerator();
        token = (await NextAsync(e0)).ResumeToken;
        await l0.DisposeAsync();   // completes the channel writer
        await e0.DisposeAsync();   // safe now that channel is completed

        // write to the collection AFTER capturing the token
        await rig.Db.WriteAsync([new SetWrite { Path = "c/b", Fields = Map() }]);
        await Task.Delay(600);                                                // let pump advance cursor

        // resume → initial full snapshot must arrive (relevant change happened)
        var resumed = rig.Db.Listen(new QueryAst("c"), new ListenOptions(ResumeFrom: token));
        var er = resumed.Snapshots().GetAsyncEnumerator();
        var snap = await NextAsync(er);
        Assert.Equal(2, snap.Documents.Count);                                // a + b
        await resumed.DisposeAsync();
        await er.DisposeAsync();
    }

    [Fact]
    public async Task Resume_TooOldToken_InitialSnapshotArrives()
    {
        await using var rig = Start();
        await rig.Db.WriteAsync([new SetWrite { Path = "c/a", Fields = Map() }]);

        // capture a resume token; dispose the listener (completing the channel) BEFORE the enumerator
        var l0 = rig.Db.Listen(new QueryAst("c"));
        var e0 = l0.Snapshots().GetAsyncEnumerator();
        var token = (await NextAsync(e0)).ResumeToken;
        await l0.DisposeAsync();
        await e0.DisposeAsync();

        // write then prune everything — token is now too old
        await rig.Db.WriteAsync([new SetWrite { Path = "c/b", Fields = Map() }]);
        await Task.Delay(400);
        var reader = new ChangeFeedReader(Fx.DataSource, Fx.Table);
        await reader.PruneBeforeAsync(DateTimeOffset.UtcNow.AddMinutes(1));

        // resume with too-old token → must NOT be suppressed (initial snapshot arrives)
        var resumed = rig.Db.Listen(new QueryAst("c"), new ListenOptions(ResumeFrom: token));
        var er = resumed.Snapshots().GetAsyncEnumerator();
        var snap = await NextAsync(er, timeoutMs: 5000);
        Assert.True(snap.Documents.Count >= 1);                               // at least 'a' or 'b' present (not suppressed)
        await resumed.DisposeAsync();
        await er.DisposeAsync();
    }

    // ── Test: coalescing ─────────────────────────────────────────────────────

    [Fact]
    public async Task Coalescing_TwoWritesBeforeRead_OnlyLatestStateDelivered()
    {
        await using var rig = Start();
        var query = new QueryAst("c");
        var listener = rig.Db.Listen(query);
        await using var e = listener.Snapshots().GetAsyncEnumerator();
        await NextAsync(e);                                                   // consume initial (empty)

        // write c/a then c/b without reading in between
        await rig.Db.WriteAsync([new SetWrite { Path = "c/a", Fields = Map() }]);
        await rig.Db.WriteAsync([new SetWrite { Path = "c/b", Fields = Map() }]);

        // wait for both writes to have been processed by the pump, then read
        await Task.Delay(1500);

        // read one snapshot — must reflect final state (both docs)
        var snap = await NextAsync(e);
        Assert.Equal(2, snap.Documents.Count);

        // dispose the listener first (completes the channel) before disposing the enumerator
        await listener.DisposeAsync();

        // no further snapshot: channel is completed, MoveNextAsync returns false
        Assert.False(await e.MoveNextAsync());
    }

    // ── Test: guarded all-filtered non-initial skip ──────────────────────────

    [Fact]
    public async Task GuardedListener_AllDenied_NoEmissionUntilVisibleDoc()
    {
        await using var rig = Start();
        var rules = new DenyPrefixEvaluator("c/secret");
        var guarded = new GuardedDocumentDatabase(rig.Db, rules);

        await using var listener = guarded.Listen(new QueryAst("c"));
        await using var e = listener.Snapshots().GetAsyncEnumerator();
        await NextAsync(e);                                                   // empty initial

        // write ONLY denied docs
        await rig.Db.WriteAsync([new SetWrite { Path = "c/secret1", Fields = Map() }]);
        await rig.Db.WriteAsync([new SetWrite { Path = "c/secret2", Fields = Map() }]);

        // no emission within 1.5s
        var next = e.MoveNextAsync().AsTask();
        Assert.NotSame(next, await Task.WhenAny(next, Task.Delay(1500)));

        // write a visible doc → snapshot arrives with just it
        await rig.Db.WriteAsync([new SetWrite { Path = "c/visible", Fields = Map() }]);
        Assert.True(await next);
        Assert.Equal("visible", Assert.Single(e.Current.Documents).Id);
    }
}

internal sealed class DenyPrefixEvaluator(string prefix) : Winche.Sentinel.Interfaces.IAccessRuleEvaluator<Winche.Database.Documents.Document>
{
    public Task EvaluateAsync(Winche.Sentinel.Models.AccessOperation operation, string path, object? data,
        Func<CancellationToken, Task<Winche.Database.Documents.Document?>>? getResource, CancellationToken ct = default)
    {
        if (operation == Winche.Sentinel.Models.AccessOperation.Read && path.StartsWith(prefix, StringComparison.Ordinal))
            throw new Winche.Sentinel.Models.AccessDeniedException(operation, path);
        return Task.CompletedTask;
    }
}
