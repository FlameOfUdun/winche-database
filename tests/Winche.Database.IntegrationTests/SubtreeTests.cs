using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Winche.Database.Abstraction;
using Winche.Database.Constants;
using Winche.Database.DependencyInjection;
using Winche.Database.Querying.Sql;
using Winche.Database.Runtime;
using Winche.Database.Runtime.ChangeFeed;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

/// <summary>10.5.0: export and delete everything under a document, page by page / batch by batch.</summary>
[Collection("postgres")]
public class SubtreeTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private DocumentDatabase Db() => new(Fx.DataSource, Options.Create(new WincheDatabaseOptions()));
    private static Dictionary<string, Value> N(int n) => new() { ["n"] = new IntegerValue(n) };

    private static readonly string[] Subtree =
    [
        "userData/u1", "userData/u1/notes/a", "userData/u1/notes/a/comments/c1", "userData/u1/notes/b",
        "userData/u1/sessionHistory/s1", "userData/u1/sessionHistory/s1/events/e1",
    ];
    private static readonly string[] Beside = ["userData/u10", "userData/u1-x/notes/z", "userData/u2/notes/a", "other/u1"];

    private async Task SeedAsync(int extraSessions = 0)
    {
        var writes = Subtree.Concat(Beside).Select((p, i) => (Write)new SetWrite { Path = p, Fields = N(i) }).ToList();
        for (var i = 0; i < extraSessions; i++) writes.Add(new SetWrite { Path = $"userData/u1/sessionHistory/x{i:D3}", Fields = N(i) });
        foreach (var chunk in writes.Chunk(400)) await Db().WriteAsync(chunk);
    }

    [Fact]
    public async Task Export_returns_the_document_and_everything_beneath_it_in_path_order()
    {
        await SeedAsync(extraSessions: 7);
        var exported = new List<string>();
        await foreach (var doc in Db().ExportSubtreeAsync("userData/u1", pageSize: 3)) exported.Add(doc.Path);

        var expected = Subtree.Concat(Enumerable.Range(0, 7).Select(i => $"userData/u1/sessionHistory/x{i:D3}"))
            .Order(StringComparer.Ordinal).ToList();
        Assert.Equal(expected, exported);   // complete, nothing beside it, paged without gaps or repeats
        Assert.Equal(new IntegerValue(1), (await Db().ExportSubtreeAsync("userData/u1/notes/a").FirstAsync()).Fields["n"]);
    }

    [Fact]
    public async Task Export_of_a_missing_root_still_returns_what_is_beneath_it()
    {
        await Db().WriteAsync([new SetWrite { Path = "ghost/g1/notes/n1", Fields = N(1) }]);
        var paths = await Db().ExportSubtreeAsync("ghost/g1").Select(d => d.Path).ToListAsync();
        Assert.Equal(["ghost/g1/notes/n1"], paths);
    }

    [Fact]
    public async Task Delete_removes_the_subtree_in_batches_deepest_first_and_spares_the_rest()
    {
        await SeedAsync(extraSessions: 20);
        var deleted = await Db().DeleteSubtreeAsync("userData/u1", batchSize: 5);

        Assert.Equal(Subtree.Length + 20, deleted);
        Assert.Empty(await Db().ExportSubtreeAsync("userData/u1").ToListAsync());
        foreach (var p in Beside) Assert.NotNull(await Db().GetAsync(p));

        // Several transactions, and a document went only after everything beneath it.
        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT document_path, txid::text FROM {WincheDatabaseTables.Changes} WHERE type = 'removed' ORDER BY seq";
        var removals = new List<(string Path, string Tx)>();
        await using (var r = await cmd.ExecuteReaderAsync()) while (await r.ReadAsync()) removals.Add((r.GetString(0), r.GetString(1)));
        Assert.True(removals.Select(x => x.Tx).Distinct().Count() >= 6);
        // A document is never removed in an earlier transaction than anything beneath it (within one
        // transaction they go together, atomically).
        ulong Tx(string p) => ulong.Parse(removals.Single(x => x.Path == p).Tx);
        foreach (var (child, _) in removals)
            foreach (var (parent, _) in removals.Where(x => child.StartsWith(x.Path + "/", StringComparison.Ordinal)))
                Assert.True(Tx(child) <= Tx(parent), $"{parent} went before {child}");
        Assert.Equal(removals.Max(x => ulong.Parse(x.Tx)), Tx("userData/u1"));   // the root in the last batch

        Assert.Equal(0, await Db().DeleteSubtreeAsync("userData/u1"));   // running again: nothing left
    }

    [Fact]
    public async Task Hooks_see_every_deleted_document()
    {
        await SeedAsync();
        var hook = new ChangeRecorder();
        var set = new ChangeFeedConsumerSet([], [new HookRegistration("userData/{uid}/{rest=**}", hook)]);
        var pump = new ChangeFeedPump(Fx.DataSource, set.All, new ChangeFeedConfig { PollInterval = TimeSpan.FromMilliseconds(100) },
            NullLogger<ChangeFeedPump>.Instance);
        using var cts = new CancellationTokenSource();
        var run = Task.Run(() => pump.RunAsync(cts.Token));
        try
        {
            var reader = new ChangeFeedReader(Fx.DataSource);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (await reader.GetPositionAsync(set.HookNames.Single()) is null && DateTime.UtcNow < deadline) await Task.Delay(50);

            await Db().DeleteSubtreeAsync("userData/u1", batchSize: 2);

            deadline = DateTime.UtcNow.AddSeconds(15);
            while (hook.Changes.Count < Subtree.Length - 1 && DateTime.UtcNow < deadline) await Task.Delay(50);
            Assert.Equal(Subtree.Skip(1).Order(StringComparer.Ordinal),   // the hook's pattern is below the root
                hook.Changes.Where(c => c.Type == ChangeType.Removed).Select(c => c.Path).Order(StringComparer.Ordinal));
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task A_collection_path_is_rejected()
    {
        await Assert.ThrowsAsync<RuntimeException>(() => Db().DeleteSubtreeAsync("userData"));
        await Assert.ThrowsAsync<RuntimeException>(async () => await Db().ExportSubtreeAsync("userData/u1/notes").ToListAsync());
    }

    [Fact]
    public async Task The_pages_use_the_path_index()
    {
        var page = DocumentSql.SubtreePage("userData/u1", "userData/u1/notes/a", 50, descending: true, pathsOnly: true);
        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        await using (var off = conn.CreateCommand()) { off.CommandText = "SET enable_seqscan = off"; await off.ExecuteNonQueryAsync(); }
        await using var explain = conn.CreateCommand();
        explain.CommandText = "EXPLAIN " + page.Sql;
        explain.Parameters.AddRange(page.Parameters);
        var plan = new List<string>();
        await using (var r = await explain.ExecuteReaderAsync()) while (await r.ReadAsync()) plan.Add(r.GetString(0));
        Assert.Contains(plan, l => l.Contains(SchemaSql.PathIndex));
    }
}
