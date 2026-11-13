using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Winche.Database.Abstraction;
using Winche.Database.Constants;
using Winche.Database.DependencyInjection;
using Winche.Database.Documents;
using Winche.Database.Runtime;
using Winche.Database.Runtime.ChangeFeed;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

/// <summary>Records every change it's given.</summary>
internal sealed class ChangeRecorder : DocumentStoreHook
{
    private readonly List<DocumentChange> _changes = [];
    public IReadOnlyList<DocumentChange> Changes { get { lock (_changes) return [.. _changes]; } }

    public override Task OnChangeAsync(DocumentChange change, CancellationToken ct)
    {
        lock (_changes) _changes.Add(change);
        return Task.CompletedTask;
    }
}

/// <summary>An old-style hook: only OnDocumentSetAsync.</summary>
internal sealed class SetOnlyHook : DocumentStoreHook
{
    public List<Document> Sets { get; } = [];
    public override Task OnDocumentSetAsync(string path, Document document, CancellationToken ct)
    {
        lock (Sets) Sets.Add(document);
        return Task.CompletedTask;
    }
}

/// <summary>10.5.0: hooks registered withSnapshots see the document before and after each change.</summary>
[Collection("postgres")]
public class HookSnapshotTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private static Dictionary<string, Value> N(int n) => new() { ["n"] = new IntegerValue(n) };
    private static long? Num(Document? d) => d is null ? null : ((IntegerValue)d.Fields["n"]).Value;

    private static async Task Eventually(Func<bool> condition, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(50);
        }
    }

    private async Task<(CancellationTokenSource Cts, Task Run)> StartAsync(params HookRegistration[] hooks)
    {
        var set = new ChangeFeedConsumerSet([], hooks);
        var pump = new ChangeFeedPump(Fx.DataSource, set.All, new ChangeFeedConfig { PollInterval = TimeSpan.FromMilliseconds(100) },
            NullLogger<ChangeFeedPump>.Instance);
        var cts = new CancellationTokenSource();
        var run = Task.Run(() => pump.RunAsync(cts.Token));
        var reader = new ChangeFeedReader(Fx.DataSource);
        await Eventually(() => set.HookNames.All(n => reader.GetPositionAsync(n).GetAwaiter().GetResult() is not null));
        return (cts, run);
    }

    [Fact]
    public async Task Each_change_carries_exactly_the_versions_it_wrote()
    {
        var hook = new ChangeRecorder();
        var (cts, run) = await StartAsync(new HookRegistration("snap/{id}", hook, WithSnapshots: true));
        try
        {
            var applier = new WriteApplier(Fx.DataSource, snapshotPaths: ["snap/{id}"]);
            await applier.ApplyAsync([new SetWrite { Path = "snap/a", Fields = N(1) }]);
            await applier.ApplyAsync([new SetWrite { Path = "snap/a", Fields = N(2) }]);
            await applier.ApplyAsync([new SetWrite { Path = "snap/a", Fields = N(3) }]);   // quick: current is 3 by delivery
            await applier.ApplyAsync([new DeleteWrite { Path = "snap/a" }]);

            await Eventually(() => hook.Changes.Count == 4);
            var c = hook.Changes;
            Assert.Equal([ChangeType.Added, ChangeType.Modified, ChangeType.Modified, ChangeType.Removed], c.Select(x => x.Type));
            Assert.Equal([(null, 1L), (1L, 2L), (2L, 3L), (3L, null)], c.Select(x => (Num(x.Before), Num(x.After))));
            Assert.Equal([1L, 2L, 3L, 3L], c.Select(x => x.Version));
            Assert.Equal(c[1].After!.Version, c[2].Before!.Version);
            Assert.Equal("snap/a", c[3].Before!.Path);
            Assert.Equal(c[0].After!.CreateTime, c[3].Before!.CreateTime);
        }
        finally { await cts.CancelAsync(); await run; }
    }

    [Fact]
    public async Task Without_snapshots_a_hook_gets_the_current_document_and_nothing_is_stored()
    {
        var hook = new ChangeRecorder();
        var (cts, run) = await StartAsync(new HookRegistration("plain/{id}", hook));
        try
        {
            var applier = new WriteApplier(Fx.DataSource, snapshotPaths: ["snap/{id}"]);   // another path has them
            await applier.ApplyAsync([new SetWrite { Path = "plain/a", Fields = N(1) }]);
            await applier.ApplyAsync([new SetWrite { Path = "plain/a", Fields = N(2) }]);
            await Eventually(() => hook.Changes.Count == 2);
            Assert.All(hook.Changes, x => Assert.Null(x.Before));
            Assert.Equal(2, Num(hook.Changes[^1].After));

            await using var conn = await Fx.DataSource.OpenConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT count(*) FROM {WincheDatabaseTables.Changes} WHERE document_path = 'plain/a' AND (before IS NOT NULL OR after IS NOT NULL)";
            Assert.Equal(0L, await cmd.ExecuteScalarAsync());
        }
        finally { await cts.CancelAsync(); await run; }
    }

    [Fact]
    public async Task An_old_style_hook_registered_with_snapshots_still_gets_its_calls()
    {
        var hook = new SetOnlyHook();
        var (cts, run) = await StartAsync(new HookRegistration("legacy/{id}", hook, WithSnapshots: true));
        try
        {
            await new WriteApplier(Fx.DataSource, snapshotPaths: ["legacy/{id}"]).ApplyAsync([new SetWrite { Path = "legacy/a", Fields = N(7) }]);
            await Eventually(() => { lock (hook.Sets) return hook.Sets.Count == 1; });
            Assert.Equal(7, Num(hook.Sets[0]));
        }
        finally { await cts.CancelAsync(); await run; }
    }

    [Fact]
    public async Task Registering_withSnapshots_makes_the_database_store_them()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWincheDatabase(o =>
        {
            o.ConnectionString = Fx.ConnectionString;
            o.UseHooks(h => h.Add<ChangeRecorder>("di/{id}", withSnapshots: true).Add<SetOnlyHook>("other/{id}"));
        });
        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<WincheDatabaseOptions>>().Value;
        Assert.Equal(["di/{id}"], options.HookSnapshotPaths);

        var db = provider.GetRequiredService<DocumentDatabase>();
        await db.WriteAsync([new SetWrite { Path = "di/x", Fields = N(1) }, new SetWrite { Path = "other/y", Fields = N(2) }]);

        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT document_path, after IS NOT NULL FROM {WincheDatabaseTables.Changes} WHERE document_path IN ('di/x', 'other/y') ORDER BY 1";
        var rows = new List<(string, bool)>();
        await using (var r = await cmd.ExecuteReaderAsync()) while (await r.ReadAsync()) rows.Add((r.GetString(0), r.GetBoolean(1)));
        Assert.Equal([("di/x", true), ("other/y", false)], rows);
    }

    private sealed class SinkRecorder : IWincheChangeSink
    {
        public List<WincheChange> Changes { get; } = [];
        public Task OnChangesAsync(IReadOnlyList<WincheChange> changes, CancellationToken ct)
        {
            lock (Changes) Changes.AddRange(changes);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Published_changes_reach_every_sink_as_plain_json()
    {
        var sink = new SinkRecorder();
        var (cts, run) = await StartAsync(new HookRegistration("pub/{id}", new ChangePublisher([sink], TimeProvider.System), WithSnapshots: true));
        try
        {
            var applier = new WriteApplier(Fx.DataSource, snapshotPaths: ["pub/{id}"]);
            await applier.ApplyAsync([new SetWrite { Path = "pub/a", Fields = N(1) }]);
            await applier.ApplyAsync([new SetWrite { Path = "pub/a", Fields = N(2) }]);
            await Eventually(() => { lock (sink.Changes) return sink.Changes.Count == 2; });
            var updated = sink.Changes[1];
            Assert.Equal(("database", "updated", "pub/a"), (updated.Source, updated.Type, updated.Path));
            Assert.Equal(2, (long)updated.Data!["fields"]!["n"]!);
            Assert.Equal(1, (long)updated.Before!["fields"]!["n"]!);
            Assert.Equal(("a", "pub"), (updated.Variables["id"], updated.Variables["collection"]));
        }
        finally
        {
            await cts.CancelAsync();
            try { await run; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public void PublishChanges_registers_the_source_and_the_database_offers_its_action()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWincheDatabase(o => { o.ConnectionString = Fx.ConnectionString; o.UseHooks(h => h.PublishChanges()); });
        using var provider = services.BuildServiceProvider();
        Assert.Contains(provider.GetServices<WincheChangeSource>(), s => s.Name == "database");
        Assert.Contains(provider.GetServices<IWincheAction>(), a => a.Name == "database.deleteDocuments");
    }
}
