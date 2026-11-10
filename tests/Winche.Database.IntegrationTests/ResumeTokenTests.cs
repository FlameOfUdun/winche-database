using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Winche.Database.Constants;
using Winche.Database.DependencyInjection;
using Winche.Database.Querying.Ast;
using Winche.Database.Runtime;
using Winche.Database.Runtime.ChangeFeed;
using Winche.Database.Runtime.Listening;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

/// <summary>
/// 10.5.0 resume tokens cover whole transactions, so a resume is never told "current" while a change that
/// committed late (after a later one) is missing — which a plain feed sequence number allowed.
/// </summary>
[Collection("postgres")]
public class ResumeTokenTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private sealed class Rig : IAsyncDisposable
    {
        public required DocumentDatabase Db { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required Task Run { get; init; }
        public async ValueTask DisposeAsync() { await Cts.CancelAsync(); await Run; }
    }

    private Rig Start()
    {
        var registry = new QueryListenerRegistry(Fx.DataSource);
        var docs = new DocumentListenerRegistry(Fx.DataSource);
        var db = new DocumentDatabase(Fx.DataSource, Options.Create(new WincheDatabaseOptions()), registry, docListeners: docs);
        var pump = new ChangeFeedPump(Fx.DataSource, [registry, docs],
            new ChangeFeedConfig { PollInterval = TimeSpan.FromMilliseconds(100) }, NullLogger<ChangeFeedPump>.Instance);
        var cts = new CancellationTokenSource();
        return new Rig { Db = db, Cts = cts, Run = Task.Run(() => pump.RunAsync(cts.Token)) };
    }

    private static async Task<T> NextAsync<T>(IAsyncEnumerator<T> e, int timeoutMs = 10000)
    {
        var move = e.MoveNextAsync().AsTask();
        Assert.True(await Task.WhenAny(move, Task.Delay(timeoutMs)) == move, "timed out waiting for a snapshot");
        Assert.True(await move);
        return e.Current;
    }

    private static Dictionary<string, Value> Map(int n) => new() { ["n"] = new IntegerValue(n) };

    /// <summary>A transaction that has written a document and its change row (taking a seq) but not committed.</summary>
    private async Task<(NpgsqlConnection Conn, NpgsqlTransaction Tx)> BeginSlowWriteAsync(string collection, string id)
    {
        var conn = await Fx.DataSource.OpenConnectionAsync();
        var tx = await conn.BeginTransactionAsync();
        await using (var doc = conn.CreateCommand())
        {
            doc.Transaction = tx;
            doc.CommandText = $$"""
                INSERT INTO {{WincheDatabaseTables.Documents}} (document_path, document_id, collection_path, collection_id, data)
                VALUES ($1, $2, $3, $3, $4::jsonb)
                """;
            doc.Parameters.AddWithValue($"{collection}/{id}");
            doc.Parameters.AddWithValue(id);
            doc.Parameters.AddWithValue(collection);
            doc.Parameters.AddWithValue("""{"n": {"integerValue": "0"}}""");
            await doc.ExecuteNonQueryAsync();
        }
        await using (var change = conn.CreateCommand())
        {
            change.Transaction = tx;
            change.CommandText = $"INSERT INTO {WincheDatabaseTables.Changes} (type, document_path, collection_path, version, commit_time) VALUES ('added', $1, $2, 1, now())";
            change.Parameters.AddWithValue($"{collection}/{id}");
            change.Parameters.AddWithValue(collection);
            await change.ExecuteNonQueryAsync();
        }
        return (conn, tx);
    }

    private static async Task<long> TokenOfFirstSnapshotAsync(DocumentDatabase db, Query query)
    {
        await using var l = db.Listen(query);
        await using var e = l.Snapshots().GetAsyncEnumerator();
        return (await NextAsync(e)).ResumeToken;
    }

    [Fact]
    public async Task A_resume_is_not_told_current_while_a_late_commit_is_missing()
    {
        await using var rig = Start();
        await Task.Delay(300);

        var (conn, tx) = await BeginSlowWriteAsync("late", "slow");                 // lower seq, still running
        await rig.Db.WriteAsync([new SetWrite { Path = "late/fast", Fields = Map(1) }]);   // higher seq, commits

        // The client's state now has "fast" but not "slow" (not committed yet).
        var token = await TokenOfFirstSnapshotAsync(rig.Db, new Query("late"));

        await tx.CommitAsync();
        await conn.DisposeAsync();
        await Task.Delay(500);   // let the pump deliver both

        // Before 10.5.0 the token was MAX(seq) = fast's seq: no row after it, so "current" — and "slow" was lost.
        await using var resumed = rig.Db.Listen(new Query("late"), new ListenOptions(ResumeFrom: token));
        await using var er = resumed.Snapshots().GetAsyncEnumerator();
        var first = await NextAsync(er);
        Assert.False(first.Current);
        Assert.Equal(["fast", "slow"], first.Documents.Select(d => d.Id).Order());
    }

    [Fact]
    public async Task A_token_from_a_delta_still_resumes_as_current()
    {
        await using var rig = Start();
        await Task.Delay(300);
        long token;
        await using (var l = rig.Db.Listen(new Query("d")))
        await using (var e = l.Snapshots().GetAsyncEnumerator())
        {
            await NextAsync(e);   // initial
            await rig.Db.WriteAsync([new SetWrite { Path = "d/a", Fields = Map(1) }]);
            var delta = await NextAsync(e);
            Assert.Single(delta.Documents);
            token = delta.ResumeToken;
        }

        await using var resumed = rig.Db.Listen(new Query("d"), new ListenOptions(ResumeFrom: token));
        await using var er = resumed.Snapshots().GetAsyncEnumerator();
        Assert.True((await NextAsync(er)).Current);
    }

    [Fact]
    public async Task A_document_listener_resumes_the_same_way()
    {
        await using var rig = Start();
        await Task.Delay(300);
        long token;
        await using (var l = await rig.Db.ListenToDocumentAsync("doc/a"))
        await using (var e = l.Snapshots().GetAsyncEnumerator())
        {
            await NextAsync(e);
            await rig.Db.WriteAsync([new SetWrite { Path = "doc/a", Fields = Map(1) }]);
            token = (await NextAsync(e)).ResumeToken;
        }

        await using (var resumed = await rig.Db.ListenToDocumentAsync("doc/a", new ListenOptions(ResumeFrom: token)))
        await using (var er = resumed.Snapshots().GetAsyncEnumerator())
            Assert.True((await NextAsync(er)).Current);

        await rig.Db.WriteAsync([new SetWrite { Path = "doc/a", Fields = Map(2) }]);
        await Task.Delay(500);
        await using var stale = await rig.Db.ListenToDocumentAsync("doc/a", new ListenOptions(ResumeFrom: token));
        await using var es = stale.Snapshots().GetAsyncEnumerator();
        var snap = await NextAsync(es);
        Assert.False(snap.Current);
        Assert.Equal(new IntegerValue(2), snap.Document!.Fields["n"]);
    }

    [Fact]
    public async Task A_token_from_before_the_upgrade_gets_a_full_snapshot()
    {
        await using var rig = Start();
        await rig.Db.WriteAsync([new SetWrite { Path = "old/a", Fields = Map(1) }]);
        await Task.Delay(300);

        await using var resumed = rig.Db.Listen(new Query("old"), new ListenOptions(ResumeFrom: 123));   // a seq-style token
        await using var er = resumed.Snapshots().GetAsyncEnumerator();
        var first = await NextAsync(er);
        Assert.False(first.Current);
        Assert.Single(first.Documents);
        Assert.True(first.ResumeToken < 0);   // and from now on a new-style token
    }

    [Fact]
    public async Task Pruning_past_a_token_makes_it_unverifiable()
    {
        var reader = new ChangeFeedReader(Fx.DataSource);
        await new WriteApplier(Fx.DataSource).ApplyAsync([new SetWrite { Path = "pr/a", Fields = Map(1) }]);
        var head = await reader.GetHeadAsync();
        Assert.True(await reader.RetainsChangesAfterAsync(head.TxId));

        await new WriteApplier(Fx.DataSource).ApplyAsync([new SetWrite { Path = "pr/b", Fields = Map(2) }]);
        await reader.PruneBeforeAsync(DateTimeOffset.UtcNow.AddMinutes(1));   // deletes both rows

        Assert.False(await reader.RetainsChangesAfterAsync(head.TxId));       // pr/b's row is gone
        var after = await reader.GetHeadAsync();
        Assert.True(await reader.RetainsChangesAfterAsync(after.TxId));
    }

    [Fact]
    public void Tokens_round_trip_and_old_ones_are_recognised()
    {
        foreach (var txid in new ulong[] { 0, 1, 42, 9_000_000_000 })
        {
            var token = ResumeTokens.Encode(txid);
            Assert.True(token < 0);
            Assert.True(ResumeTokens.TryDecode(token, out var back));
            Assert.Equal(txid, back);
        }
        Assert.False(ResumeTokens.TryDecode(0, out _));
        Assert.False(ResumeTokens.TryDecode(57, out _));
    }

    [Fact]
    public void After_a_batch_the_token_covers_its_last_transaction_only_when_it_is_whole()
    {
        ChangeRecord R(ulong tx, long seq) => new(seq, ChangeType.Added, "c/x", "c", 1, DateTimeOffset.UtcNow) { TxId = tx };
        var docs = new Dictionary<string, Documents.Document>();
        Assert.Equal(ResumeTokens.Encode(7), ResumeTokens.After(new ChangeBatch([R(5, 1), R(7, 2)], docs) { EndsWithWholeTransaction = true }));
        Assert.Equal(ResumeTokens.Encode(6), ResumeTokens.After(new ChangeBatch([R(5, 1), R(7, 2)], docs)));
    }
}
