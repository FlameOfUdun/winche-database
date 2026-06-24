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
public class ListenToDocumentTests(PostgresFixture fx) : QueryTestBase(fx)
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
        var registry = new ListenerRegistry(Fx.DataSource);
        var db = new DocumentDatabase(Fx.DataSource, opts, registry);
        var pump = new ChangeFeedPump(Fx.DataSource, [registry],
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
    public async Task ListenToDocument_ReflectsCreateUpdateDelete()
    {
        await using var rig = Start();
        IDocumentDatabase db = rig.Db;

        await using var listener = db.ListenToDocument("c/a");
        await using var e = listener.Snapshots().GetAsyncEnumerator();

        var initial = await NextAsync(e);
        Assert.False(initial.Exists);
        Assert.Null(initial.Document);

        await rig.Db.WriteAsync([new SetWrite { Path = "c/a", Fields = Map(("n", new IntegerValue(1))) }]);
        var created = await NextAsync(e);
        Assert.True(created.Exists);
        Assert.Equal(new IntegerValue(1), created.Document!.Fields["n"]);

        await rig.Db.WriteAsync([new SetWrite { Path = "c/a", Fields = Map(("n", new IntegerValue(2))) }]);
        var updated = await NextAsync(e);
        Assert.Equal(new IntegerValue(2), updated.Document!.Fields["n"]);

        await rig.Db.WriteAsync([new DeleteWrite { Path = "c/a" }]);
        var deleted = await NextAsync(e);
        Assert.False(deleted.Exists);
        Assert.Null(deleted.Document);
    }

    [Fact]
    public async Task ListenToDocument_InvalidPath_Throws()
    {
        await using var rig = Start();
        IDocumentDatabase db = rig.Db;
        // "c" is a collection path (even slash count), not a document path.
        Assert.Throws<RuntimeException>(() => db.ListenToDocument("c"));
    }
}
