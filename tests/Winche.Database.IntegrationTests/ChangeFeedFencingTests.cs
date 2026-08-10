using Npgsql;
using Winche.Database.Runtime.ChangeFeed;

namespace Winche.Database.IntegrationTests;

/// <summary>
/// Direct regression coverage for the xid8 fencing fix: a change row is consumable only once its
/// writing transaction is provably settled (xid &lt; pg_snapshot_xmin(pg_current_snapshot())), and the
/// cursor is the pair (xid, seq) rather than a bare seq. This is the exact scenario that used to lose
/// a record — see the (deleted) "Known seq/commit-order race" paragraph this replaces.
/// </summary>
[Collection("postgres")]
public class ChangeFeedFencingTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private ChangeFeedReader Reader() => new(Fx.DataSource);

    private const string InsertSql = """
        INSERT INTO winche_documents_changes (type, document_path, collection_path, version, commit_time)
        VALUES ('added', $1, $2, 1, now())
        RETURNING seq, xid::text
        """;

    [Fact]
    public async Task EarlierUncommittedTransaction_BlocksBothRows_UntilItCommits()
    {
        // Open the "early" transaction and insert WITHOUT committing — raw SQL against the changes
        // table is the only way to hold a row uncommitted for the test. It still occupies the xmin
        // watermark even though its row is invisible to any other snapshot.
        await using var earlyConn = new NpgsqlConnection(Fx.ConnectionString);
        await earlyConn.OpenAsync();
        await using var earlyTx = await earlyConn.BeginTransactionAsync();
        var (earlySeq, earlyXid) = await Insert(earlyConn, "fence/a", "fence", earlyTx);

        // A second, LATER transaction commits immediately.
        var (laterSeq, laterXid) = await InsertAndCommit("fence/b", "fence");
        Assert.True(ulong.Parse(laterXid) > ulong.Parse(earlyXid), "test setup: later insert must get a higher xid");

        // Neither row is delivered while the early transaction is still open: the later row is
        // blocked because its xid is not below xmin (the early transaction still holds xmin down),
        // and the early row's own xid is (by definition) never below its own snapshot's xmin either.
        var beforeCommit = await Reader().ReadAfterAsync(ChangeCursor.Zero, 10);
        Assert.Empty(beforeCommit);

        await earlyTx.CommitAsync();

        // Both rows are now settled and are delivered together, in (xid, seq) order — the early
        // (lower-xid) row first even though it committed SECOND. This is the record that the old
        // bare-seq cursor would have lost forever if a consumer had drained between the two commits.
        var afterCommit = await Reader().ReadAfterAsync(ChangeCursor.Zero, 10);
        Assert.Equal(2, afterCommit.Count);
        Assert.Equal(["fence/a", "fence/b"], afterCommit.Select(r => r.Path));
        Assert.Equal(ulong.Parse(earlyXid), afterCommit[0].Xid);
        Assert.Equal(earlySeq, afterCommit[0].Seq);
        Assert.Equal(ulong.Parse(laterXid), afterCommit[1].Xid);
        Assert.Equal(laterSeq, afterCommit[1].Seq);
    }

    /// <summary>
    /// Round-trip: SaveCursorAsync/GetCursorAsync preserve the (xid, seq) pair, and the monotonic
    /// guard rejects a backwards save (the cursor is left at the higher pair, never regresses).
    /// </summary>
    [Fact]
    public async Task Cursor_RoundTrips_AndMonotonicGuardRejectsBackwardsSave()
    {
        var reader = Reader();
        var consumer = $"rt_{Guid.NewGuid():N}";
        Assert.Null(await reader.GetCursorAsync(consumer));

        var cursor = new ChangeCursor(1_000, 5);
        await reader.SaveCursorAsync(consumer, cursor);
        Assert.Equal(cursor, await reader.GetCursorAsync(consumer));

        // Lower xid, higher seq: still "behind" under (xid, seq) lexicographic order — must not move.
        await reader.SaveCursorAsync(consumer, new ChangeCursor(500, 999_999));
        Assert.Equal(cursor, await reader.GetCursorAsync(consumer));

        // Same xid, lower seq: also "behind" — must not move.
        await reader.SaveCursorAsync(consumer, new ChangeCursor(1_000, 1));
        Assert.Equal(cursor, await reader.GetCursorAsync(consumer));

        // Forward save (same xid, higher seq) advances normally.
        var forward = new ChangeCursor(1_000, 50);
        await reader.SaveCursorAsync(consumer, forward);
        Assert.Equal(forward, await reader.GetCursorAsync(consumer));

        // Forward save on a higher xid advances too, even with a lower seq.
        var laterXid = new ChangeCursor(2_000, 1);
        await reader.SaveCursorAsync(consumer, laterXid);
        Assert.Equal(laterXid, await reader.GetCursorAsync(consumer));
    }

    private static async Task<(long Seq, string Xid)> Insert(
        NpgsqlConnection conn, string path, string collection, NpgsqlTransaction tx)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = InsertSql;
        cmd.Parameters.AddWithValue(path);
        cmd.Parameters.AddWithValue(collection);
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return (r.GetInt64(0), r.GetString(1));
    }

    private async Task<(long Seq, string Xid)> InsertAndCommit(string path, string collection)
    {
        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = InsertSql;
        cmd.Parameters.AddWithValue(path);
        cmd.Parameters.AddWithValue(collection);
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return (r.GetInt64(0), r.GetString(1));
    }
}
