using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Winche.Database.DependencyInjection;
using Winche.Database.Runtime;
using Winche.Database.Runtime.ChangeFeed;
using Winche.Database.Runtime.Listening;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class DocumentListenerRegistryTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private static Dictionary<string, Value> Map(params (string K, Value V)[] e) => e.ToDictionary(x => x.K, x => x.V);

    private sealed class Rig : IAsyncDisposable
    {
        public required DocumentDatabase Db { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required Task Run { get; init; }
        public async ValueTask DisposeAsync() { Cts.Cancel(); await Run; }
    }

    private Rig Start()
    {
        var opts = Options.Create(new WincheDatabaseOptions());
        var queryRegistry = new QueryListenerRegistry(Fx.DataSource);
        var docRegistry = new DocumentListenerRegistry(Fx.DataSource);
        var db = new DocumentDatabase(Fx.DataSource, opts, queryRegistry, docListeners: docRegistry);
        var pump = new ChangeFeedPump(Fx.DataSource, [queryRegistry, docRegistry],
            new ChangeFeedConfig { PollInterval = TimeSpan.FromMilliseconds(200) },
            NullLogger<ChangeFeedPump>.Instance);
        var cts = new CancellationTokenSource();
        return new Rig { Db = db, Cts = cts, Run = Task.Run(() => pump.RunAsync(cts.Token)) };
    }

    private static async Task<DocumentSnapshot> NextAsync(IAsyncEnumerator<DocumentSnapshot> e, int timeoutMs = 10000)
    {
        var move = e.MoveNextAsync().AsTask();
        Assert.True(await Task.WhenAny(move, Task.Delay(timeoutMs)) == move, "timed out waiting for snapshot");
        Assert.True(await move, "snapshot stream ended unexpectedly");
        return e.Current;
    }

    [Fact]
    public async Task DocumentListener_ReflectsCreateUpdateDelete()
    {
        await using var rig = Start();
        IDocumentDatabase db = rig.Db;

        await using var listener = await db.ListenToDocumentAsync("dlr/a");
        await using var e = listener.Snapshots().GetAsyncEnumerator();

        var initial = await NextAsync(e);
        Assert.False(initial.Exists);
        Assert.Null(initial.Document);

        await rig.Db.WriteAsync([new SetWrite { Path = "dlr/a", Fields = Map(("n", new IntegerValue(1))) }]);
        var created = await NextAsync(e);
        Assert.True(created.Exists);
        Assert.Equal(new IntegerValue(1), created.Document!.Fields["n"]);

        await rig.Db.WriteAsync([new SetWrite { Path = "dlr/a", Fields = Map(("n", new IntegerValue(2))) }]);
        var updated = await NextAsync(e);
        Assert.Equal(new IntegerValue(2), updated.Document!.Fields["n"]);

        await rig.Db.WriteAsync([new DeleteWrite { Path = "dlr/a" }]);
        var deleted = await NextAsync(e);
        Assert.False(deleted.Exists);
        Assert.Null(deleted.Document);
    }

    [Fact]
    public async Task DocumentListener_IgnoresOtherDocsInSameCollection()
    {
        await using var rig = Start();
        IDocumentDatabase db = rig.Db;

        await using var listener = await db.ListenToDocumentAsync("dlr2/target");
        await using var e = listener.Snapshots().GetAsyncEnumerator();
        var initial = await NextAsync(e);
        Assert.False(initial.Exists);

        // A sibling write must NOT produce a snapshot for the target listener.
        await rig.Db.WriteAsync([new SetWrite { Path = "dlr2/other", Fields = Map(("n", new IntegerValue(9))) }]);
        await rig.Db.WriteAsync([new SetWrite { Path = "dlr2/target", Fields = Map(("n", new IntegerValue(1))) }]);

        var next = await NextAsync(e);
        Assert.True(next.Exists);
        Assert.Equal(new IntegerValue(1), next.Document!.Fields["n"]);  // first delivered snapshot is the target, not the sibling
    }

    /// <summary>
    /// Resume suppression is PATH-scoped (unlike a collection query): resuming with a token after which
    /// the target document did not change suppresses the initial snapshot, and a write to a SIBLING does
    /// not lift the suppression — only a change to the target document delivers a fresh snapshot.
    /// </summary>
    [Fact]
    public async Task DocumentListener_Resume_SiblingChangeStaysSuppressed_TargetChangeDelivers()
    {
        await using var rig = Start();
        IDocumentDatabase db = rig.Db;

        await rig.Db.WriteAsync([new SetWrite { Path = "dlr3/a", Fields = Map(("n", new IntegerValue(1))) }]);

        // Capture a resume token from a first listener on the target.
        long token;
        await using (var l = await db.ListenToDocumentAsync("dlr3/a"))
        await using (var e0 = l.Snapshots().GetAsyncEnumerator())
            token = (await NextAsync(e0)).ResumeToken;

        // Resume with no change to the target → initial snapshot suppressed (stays silent)…
        await using var resumed = await db.ListenToDocumentAsync("dlr3/a", new ListenOptions(ResumeFrom: token));
        await using var er = resumed.Snapshots().GetAsyncEnumerator();
        var pending = er.MoveNextAsync().AsTask();

        // …even after a SIBLING write (path-scoped resume: sibling changes are irrelevant).
        await rig.Db.WriteAsync([new SetWrite { Path = "dlr3/b", Fields = Map(("n", new IntegerValue(9))) }]);
        Assert.NotSame(pending, await Task.WhenAny(pending, Task.Delay(1500)));   // still pending → suppressed

        // A write to the TARGET lifts suppression → snapshot arrives with the latest state.
        await rig.Db.WriteAsync([new SetWrite { Path = "dlr3/a", Fields = Map(("n", new IntegerValue(2))) }]);
        Assert.True(await pending);
        Assert.True(er.Current.Exists);
        Assert.Equal(new IntegerValue(2), er.Current.Document!.Fields["n"]);
    }

    /// <summary>
    /// Resume with a relevant change: when the target document changed after the resume token, the
    /// initial snapshot is NOT suppressed and arrives carrying the latest state.
    /// </summary>
    [Fact]
    public async Task DocumentListener_Resume_TargetChangedBeforeResume_DeliversSnapshot()
    {
        await using var rig = Start();
        IDocumentDatabase db = rig.Db;

        await rig.Db.WriteAsync([new SetWrite { Path = "dlr4/a", Fields = Map(("n", new IntegerValue(1))) }]);

        // Capture a token, then dispose the listener (completing the channel) before the enumerator.
        var l0 = await db.ListenToDocumentAsync("dlr4/a");
        var e0 = l0.Snapshots().GetAsyncEnumerator();
        var token = (await NextAsync(e0)).ResumeToken;
        await l0.DisposeAsync();
        await e0.DisposeAsync();

        // Change the TARGET after the token, and let the pump advance its cursor.
        await rig.Db.WriteAsync([new SetWrite { Path = "dlr4/a", Fields = Map(("n", new IntegerValue(2))) }]);
        await Task.Delay(600);

        // Resume → relevant change happened, so the initial snapshot arrives with the latest state.
        await using var resumed = await db.ListenToDocumentAsync("dlr4/a", new ListenOptions(ResumeFrom: token));
        await using var er = resumed.Snapshots().GetAsyncEnumerator();
        var snap = await NextAsync(er);
        Assert.True(snap.Exists);
        Assert.Equal(new IntegerValue(2), snap.Document!.Fields["n"]);
    }
}
