using Npgsql;
using Winche.Database.Constants;
using Winche.Database.Querying.Sql;
using Winche.Database.Runtime.ChangeFeed;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

/// <summary>
/// seq is assigned before a transaction commits, so commits can arrive out of seq order. A reader must not
/// move past a record whose transaction is still running, or that record is skipped for good.
/// </summary>
[Collection("postgres")]
public class ChangeFeedOrderingTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private ChangeFeedReader Reader() => new(Fx.DataSource);
    private WriteApplier Applier() => new(Fx.DataSource);

    /// <summary>Starts a transaction that has written a change row (taking a seq) but not committed.</summary>
    private async Task<(NpgsqlConnection Conn, NpgsqlTransaction Tx)> BeginSlowWriteAsync(string path)
    {
        var conn = await Fx.DataSource.OpenConnectionAsync();
        var tx = await conn.BeginTransactionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"INSERT INTO {WincheDatabaseTables.Changes} (type, document_path, collection_path, version, commit_time) VALUES ('added', $1, 'c', 1, now())";
        cmd.Parameters.AddWithValue(path);
        await cmd.ExecuteNonQueryAsync();
        return (conn, tx);
    }

    [Fact]
    public async Task A_record_that_commits_after_a_later_one_is_not_skipped()
    {
        var start = await Reader().GetHeadAsync();
        var (conn, tx) = await BeginSlowWriteAsync("c/slow");                                   // takes seq N
        await Applier().ApplyAsync([new SetWrite { Path = "c/fast", Fields = new Dictionary<string, Value>() }]); // seq N+1, commits

        // The old seq read hands out c/fast alone; a consumer saving that cursor would never see c/slow.
#pragma warning disable CS0618
        var unfenced = await Reader().ReadAfterAsync(0, 10);
#pragma warning restore CS0618
        Assert.Equal(["c/fast"], unfenced.Select(r => r.Path));

        // The fenced read holds back until the older transaction finishes...
        Assert.Empty(await Reader().ReadAfterAsync(start, 10));

        await tx.CommitAsync();
        await conn.DisposeAsync();

        // ...then delivers both, the earlier transaction first.
        var records = await Reader().ReadAfterAsync(start, 10);
        Assert.Equal(["c/slow", "c/fast"], records.Select(r => r.Path));
        Assert.Empty(await Reader().ReadAfterAsync(records[^1].Position, 10));
    }

    [Fact]
    public async Task A_rolled_back_transaction_stops_holding_back_the_feed()
    {
        var start = await Reader().GetHeadAsync();
        var (conn, tx) = await BeginSlowWriteAsync("c/aborted");
        await Applier().ApplyAsync([new SetWrite { Path = "c/after", Fields = new Dictionary<string, Value>() }]);
        Assert.Empty(await Reader().ReadAfterAsync(start, 10));

        await tx.RollbackAsync();
        await conn.DisposeAsync();

        Assert.Equal(["c/after"], (await Reader().ReadAfterAsync(start, 10)).Select(r => r.Path));
    }

    [Fact]
    public async Task Starting_from_now_includes_a_transaction_still_running_and_skips_history()
    {
        await Applier().ApplyAsync([new SetWrite { Path = "c/history", Fields = new Dictionary<string, Value>() }]);
        var (conn, tx) = await BeginSlowWriteAsync("c/inflight");

        var head = await Reader().GetHeadAsync();
        await tx.CommitAsync();
        await conn.DisposeAsync();

        Assert.Equal(["c/inflight"], (await Reader().ReadAfterAsync(head, 10)).Select(r => r.Path));
    }

    [Fact]
    public async Task Saved_positions_never_move_backwards()
    {
        await Reader().SavePositionAsync("hook", new FeedPosition(10, 5));
        await Reader().SavePositionAsync("hook", new FeedPosition(9, 50));
        Assert.Equal(new FeedPosition(10, 5), await Reader().GetPositionAsync("hook"));
        await Reader().SavePositionAsync("hook", new FeedPosition(10, 6));
        Assert.Equal(new FeedPosition(10, 6), await Reader().GetPositionAsync("hook"));
    }

    [Fact]
    public async Task Upgrading_a_feed_without_txid_keeps_cursors_where_they_were()
    {
        async Task Exec(string sql)
        {
            await using var cmd = Fx.DataSource.CreateCommand(sql);
            await cmd.ExecuteNonQueryAsync();
        }

        // The schema as it was before this change.
        await Exec($"ALTER TABLE {WincheDatabaseTables.Changes} DROP COLUMN txid; ALTER TABLE {WincheDatabaseTables.FeedCursors} DROP COLUMN txid;");
        try
        {
            for (var i = 0; i < 3; i++)
                await Applier().ApplyAsync([new SetWrite { Path = $"c/old{i}", Fields = new Dictionary<string, Value>() }]);
            await using (var cmd = Fx.DataSource.CreateCommand($"SELECT MIN(seq) FROM {WincheDatabaseTables.Changes}"))
            {
                var first = (long)(await cmd.ExecuteScalarAsync())!;
                await Exec($"INSERT INTO {WincheDatabaseTables.FeedCursors} (consumer, seq) VALUES ('hook', {first})"); // delivered old0
            }
        }
        finally
        {
            await Exec(SchemaSql.ChangesDdl()); // the upgrade
        }

        var cursor = await Reader().GetPositionAsync("hook");
        Assert.Equal(0UL, cursor!.Value.TxId);
        await Applier().ApplyAsync([new SetWrite { Path = "c/new", Fields = new Dictionary<string, Value>() }]);

        var records = await Reader().ReadAfterAsync(cursor.Value, 10);
        Assert.Equal(["c/old1", "c/old2", "c/new"], records.Select(r => r.Path));
        Assert.True(records[^1].TxId > 0);
    }
}
