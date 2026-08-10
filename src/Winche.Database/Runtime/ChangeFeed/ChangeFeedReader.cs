using Npgsql;
using Winche.Database.Constants;
using Winche.Database.Documents;

namespace Winche.Database.Runtime.ChangeFeed;

/// <summary>Reads the durable feed.</summary>
public sealed class ChangeFeedReader(NpgsqlDataSource source)
{
    /// <summary>
    /// Reads up to <paramref name="limit"/> change records after <paramref name="cursor"/>, ordered by
    /// (xid, seq).
    ///
    /// <para><b>Fencing guarantee:</b> a row is returned only once its writing transaction is provably
    /// settled — <c>xid &lt; pg_snapshot_xmin(pg_current_snapshot())</c> means no transaction with that
    /// xid (or lower) can still be in flight, so the row can never be followed by a same-or-lower-xid
    /// row committing later. The cursor comparison <c>(xid, seq) &gt; ($1, $2)</c> never advances past
    /// an xid that could still produce more rows, so a transaction that committed late (after a
    /// higher-xid transaction was already read) is still delivered on a later poll — this is what
    /// closes the seq/commit-order race described in the old version of this doc comment: two
    /// concurrent writers assigning seq 5 and seq 6 could commit out of order, and a reader that
    /// advanced past 6 immediately would never see 5. The row-constructor comparison also makes
    /// pagination exact even when one transaction's change set is larger than <paramref name="limit"/>
    /// — the next page resumes mid-transaction with no truncation special-casing.</para>
    /// </summary>
    public async Task<IReadOnlyList<ChangeRecord>> ReadAfterAsync(ChangeCursor cursor, int limit, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT xid::text, seq, type, document_path, collection_path, version, commit_time
            FROM {WincheDatabaseTables.Changes}
            WHERE (xid, seq) > ($1::text::xid8, $2) AND xid < pg_snapshot_xmin(pg_current_snapshot())
            ORDER BY xid, seq
            LIMIT $3
            """;
        cmd.Parameters.AddWithValue(cursor.Xid.ToString());
        cmd.Parameters.AddWithValue(cursor.Seq);
        cmd.Parameters.AddWithValue(limit);

        var records = new List<ChangeRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            records.Add(new ChangeRecord(
                ulong.Parse(reader.GetString(0)),
                reader.GetInt64(1),
                reader.GetString(2) switch
                {
                    "added" => ChangeType.Added,
                    "modified" => ChangeType.Modified,
                    "removed" => ChangeType.Removed,
                    var other => throw new InvalidOperationException($"Unknown change type '{other}'"),
                },
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.GetFieldValue<DateTimeOffset>(6)));
        }
        return records;
    }

    /// <summary>
    /// The cursor a fresh consumer should boot from: "live from here" without a bare-MAX(seq) pin,
    /// which would skip any transaction still in flight at boot. <c>pg_snapshot_xmin</c> is the
    /// oldest xid that might still be in flight, so a row from that (or a later) transaction has
    /// not necessarily been observed yet; pairing it with seq 0 means "nothing at or after this xid
    /// has been consumed" — the very next poll's fencing filter (<see cref="ReadAfterAsync"/>) picks
    /// up such a row once its transaction actually settles.
    /// </summary>
    public async Task<ChangeCursor> GetWatermarkAsync(CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pg_snapshot_xmin(pg_current_snapshot())::text";
        var xmin = (string)(await cmd.ExecuteScalarAsync(ct))!;
        return new ChangeCursor(ulong.Parse(xmin), 0);
    }

    /// <summary>True when any settled feed row after <paramref name="cursor"/> touches the collection.</summary>
    public Task<bool> HasQueryChangesAfterAsync(ChangeCursor cursor, string collection, CancellationToken ct = default) =>
        ExistsAfterAsync(cursor, "collection_path", collection, ct);

    /// <summary>True when any settled feed row after <paramref name="cursor"/> touches the exact document path.</summary>
    public Task<bool> HasDocumentChangesAfterAsync(ChangeCursor cursor, string documentPath, CancellationToken ct = default) =>
        ExistsAfterAsync(cursor, "document_path", documentPath, ct);

    // column MUST be a trusted constant (never user input) — it is interpolated into the SQL text.
    private async Task<bool> ExistsAfterAsync(ChangeCursor cursor, string column, string value, CancellationToken ct)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        // Same settled filter as ReadAfterAsync — an unsettled row must not be reported as a pending
        // change here either, or a resuming listener would wrongly conclude it needs to reset/requery
        // for a row it still cannot actually see (and would see correctly on a later poll instead).
        cmd.CommandText = $"""
            SELECT EXISTS(
                SELECT 1 FROM {WincheDatabaseTables.Changes}
                WHERE (xid, seq) > ($1::text::xid8, $2) AND xid < pg_snapshot_xmin(pg_current_snapshot()) AND {column} = $3
            )
            """;
        cmd.Parameters.AddWithValue(cursor.Xid.ToString());
        cmd.Parameters.AddWithValue(cursor.Seq);
        cmd.Parameters.AddWithValue(value);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Smallest seq still in the feed, or 0 when empty. Retention-window check only — unaffected by xid fencing.</summary>
    public async Task<long> GetMinSeqAsync(CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COALESCE(MIN(seq), 0) FROM {WincheDatabaseTables.Changes}";
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<ChangeCursor?> GetCursorAsync(string consumer, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT xid::text, seq FROM {WincheDatabaseTables.FeedCursors} WHERE consumer = $1";
        cmd.Parameters.AddWithValue(consumer);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new ChangeCursor(ulong.Parse(reader.GetString(0)), reader.GetInt64(1));
    }

    public async Task SaveCursorAsync(string consumer, ChangeCursor cursor, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        // Monotonic guard, generalized to the (xid, seq) pair via row-constructor comparison: a
        // backwards save can only cause redelivery (cursor stays at the higher pair), never a
        // permanent skip.
        cmd.CommandText = $"""
            INSERT INTO {WincheDatabaseTables.FeedCursors} (consumer, xid, seq, updated_at)
            VALUES ($1, $2::text::xid8, $3, now())
            ON CONFLICT (consumer) DO UPDATE SET
                xid = CASE WHEN (EXCLUDED.xid, EXCLUDED.seq) > ({WincheDatabaseTables.FeedCursors}.xid, {WincheDatabaseTables.FeedCursors}.seq)
                           THEN EXCLUDED.xid ELSE {WincheDatabaseTables.FeedCursors}.xid END,
                seq = CASE WHEN (EXCLUDED.xid, EXCLUDED.seq) > ({WincheDatabaseTables.FeedCursors}.xid, {WincheDatabaseTables.FeedCursors}.seq)
                           THEN EXCLUDED.seq ELSE {WincheDatabaseTables.FeedCursors}.seq END,
                updated_at = now()
            """;
        cmd.Parameters.AddWithValue(consumer);
        cmd.Parameters.AddWithValue(cursor.Xid.ToString());
        cmd.Parameters.AddWithValue(cursor.Seq);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Retention: deletes rows with commit_time older than the cutoff. Returns rows deleted.</summary>
    public async Task<int> PruneBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {WincheDatabaseTables.Changes} WHERE commit_time < $1";
        cmd.Parameters.AddWithValue(cutoff);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Fetches the still-existing documents for the added/modified paths in <paramref name="records"/> (one batch query); removed paths are skipped.</summary>
    public async Task<IReadOnlyDictionary<string, Document>> FetchDocumentsAsync(
        IReadOnlyList<ChangeRecord> records, CancellationToken ct = default)
    {
        var paths = records.Where(r => r.Type != ChangeType.Removed)
            .Select(r => r.Path).Distinct(StringComparer.Ordinal).ToList();
        if (paths.Count == 0) return new Dictionary<string, Document>(StringComparer.Ordinal);
        await using var conn = await source.OpenConnectionAsync(ct);
        return await new DocumentOperations(conn, null).GetManyAsync(paths, ct);
    }
}
