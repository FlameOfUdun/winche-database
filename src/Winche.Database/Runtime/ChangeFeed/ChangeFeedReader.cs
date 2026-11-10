using Npgsql;
using Winche.Database.Constants;
using Winche.Database.Documents;

namespace Winche.Database.Runtime.ChangeFeed;

/// <summary>Reads the durable feed.</summary>
public sealed class ChangeFeedReader(NpgsqlDataSource source)
{
    /// <summary>
    /// Reads up to <paramref name="limit"/> records after <paramref name="after"/>, in (txid, seq) order, from
    /// transactions that have finished. This is gap-free: <c>seq</c> is assigned before a transaction commits,
    /// so commits arrive out of seq order, but every transaction older than the oldest one still running
    /// (<c>pg_snapshot_xmin</c>) is done, so no row can still appear below that line. A position read from here
    /// never has to be revisited. The cost: a long write transaction holds back delivery (never loses it)
    /// until it finishes. Within a transaction records stay in seq order; across transactions the order is
    /// by transaction id, which for the same document matches commit order only loosely — consumers read the
    /// document's current state, so they see the latest version either way.
    /// </summary>
    public async Task<IReadOnlyList<ChangeRecord>> ReadAfterAsync(FeedPosition after, int limit, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT seq, type, document_path, collection_path, version, commit_time, txid::text
            FROM {WincheDatabaseTables.Changes}
            WHERE (txid, seq) > ($1::text::xid8, $2)
              AND txid < pg_snapshot_xmin(pg_current_snapshot())
            ORDER BY txid, seq
            LIMIT $3
            """;
        cmd.Parameters.AddWithValue(after.TxId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue(after.Seq);
        cmd.Parameters.AddWithValue(limit);
        return await ReadRecordsAsync(cmd, ct);
    }

    /// <summary>
    /// The position of "now": everything written by a transaction that has already finished counts as
    /// before it, so reading from here delivers only what is still running or yet to come.
    /// </summary>
    public async Task<FeedPosition> GetHeadAsync(CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT pg_snapshot_xmin(pg_current_snapshot())::text";
        var xmin = ulong.Parse((string)(await cmd.ExecuteScalarAsync(ct))!, System.Globalization.CultureInfo.InvariantCulture);
        return new FeedPosition(xmin - 1, long.MaxValue);
    }

    /// <summary>A durable consumer's saved position; a cursor saved before txid was recorded reads as txid 0.</summary>
    public async Task<FeedPosition?> GetPositionAsync(string consumer, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT seq, COALESCE(txid, '0')::text FROM {WincheDatabaseTables.FeedCursors} WHERE consumer = $1";
        cmd.Parameters.AddWithValue(consumer);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new FeedPosition(ulong.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture), reader.GetInt64(0));
    }

    /// <summary>Saves a durable consumer's position; never moves it backwards.</summary>
    public async Task SavePositionAsync(string consumer, FeedPosition position, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {WincheDatabaseTables.FeedCursors} AS c (consumer, seq, txid, updated_at) VALUES ($1, $2, $3::text::xid8, now())
            ON CONFLICT (consumer) DO UPDATE SET seq = EXCLUDED.seq, txid = EXCLUDED.txid, updated_at = now()
            WHERE (COALESCE(c.txid, '0'), c.seq) < (EXCLUDED.txid, EXCLUDED.seq)
            """;
        cmd.Parameters.AddWithValue(consumer);
        cmd.Parameters.AddWithValue(position.Seq);
        cmd.Parameters.AddWithValue(position.TxId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Reads up to <paramref name="limit"/> change records with seq &gt; <paramref name="afterSeq"/>, ordered by seq.
    /// Superseded by <see cref="ReadAfterAsync(FeedPosition, int, CancellationToken)"/>, which the feed itself uses.
    ///
    /// <para><b>Known seq/commit-order race:</b> PostgreSQL assigns <c>seq</c> values from a sequence before
    /// a transaction commits. Two concurrent write transactions can therefore commit out of seq order:
    /// transaction A (seq 5) may commit <em>after</em> transaction B (seq 6). A reader that drains
    /// immediately after B commits will process seq 6 and advance its cursor past 5; when A subsequently
    /// commits, seq 5 is never re-read and the cursor permanently skips it (the skip is persisted via
    /// <see cref="SaveCursorAsync"/>). The window is the p99 write transaction duration — typically
    /// sub-millisecond on a healthy cluster. Proper fencing (e.g. snapshot-xmin) is out of scope;
    /// consumers that require strict exactly-once semantics must perform idempotency checks by version.</para>
    /// </summary>
    [Obsolete("Can skip a record that commits after a later one; use ReadAfterAsync(FeedPosition, ...).")]
    public async Task<IReadOnlyList<ChangeRecord>> ReadAfterAsync(long afterSeq, int limit, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT seq, type, document_path, collection_path, version, commit_time, txid::text FROM {WincheDatabaseTables.Changes} WHERE seq > $1 ORDER BY seq LIMIT $2";
        cmd.Parameters.AddWithValue(afterSeq);
        cmd.Parameters.AddWithValue(limit);
        return await ReadRecordsAsync(cmd, ct);
    }

    private static async Task<IReadOnlyList<ChangeRecord>> ReadRecordsAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var records = new List<ChangeRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            records.Add(new ChangeRecord(
                reader.GetInt64(0),
                reader.GetString(1) switch
                {
                    "added" => ChangeType.Added,
                    "modified" => ChangeType.Modified,
                    "removed" => ChangeType.Removed,
                    var other => throw new InvalidOperationException($"Unknown change type '{other}'"),
                },
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetFieldValue<DateTimeOffset>(5))
            {
                TxId = ulong.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture),
            });
        }
        return records;
    }

    public async Task<long> GetMaxSeqAsync(CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COALESCE(MAX(seq), 0) FROM {WincheDatabaseTables.Changes}";
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>True when any feed row after <paramref name="afterSeq"/> touches the collection.</summary>
    public Task<bool> HasQueryChangesAfterAsync(long afterSeq, string collection, CancellationToken ct = default) =>
        ExistsAfterAsync(afterSeq, "collection_path", collection, ct);

    /// <summary>True when any feed row after <paramref name="afterSeq"/> touches the exact document path.</summary>
    public Task<bool> HasDocumentChangesAfterAsync(long afterSeq, string documentPath, CancellationToken ct = default) =>
        ExistsAfterAsync(afterSeq, "document_path", documentPath, ct);

    // column MUST be a trusted constant (never user input) — it is interpolated into the SQL text.
    private async Task<bool> ExistsAfterAsync(long afterSeq, string column, string value, CancellationToken ct)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT EXISTS(SELECT 1 FROM {WincheDatabaseTables.Changes} WHERE seq > $1 AND {column} = $2)";
        cmd.Parameters.AddWithValue(afterSeq);
        cmd.Parameters.AddWithValue(value);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Smallest seq still in the feed, or 0 when empty.</summary>
    public async Task<long> GetMinSeqAsync(CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COALESCE(MIN(seq), 0) FROM {WincheDatabaseTables.Changes}";
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<long?> GetCursorAsync(string consumer, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT seq FROM {WincheDatabaseTables.FeedCursors} WHERE consumer = $1";
        cmd.Parameters.AddWithValue(consumer);
        return await cmd.ExecuteScalarAsync(ct) is long seq ? seq : null;
    }

    public async Task SaveCursorAsync(string consumer, long seq, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        // GREATEST guard: a backwards save (due to the seq/commit-order race) can only cause
        // redelivery (cursor stays at the higher value), never a permanent skip.
        cmd.CommandText = $"""
            INSERT INTO {WincheDatabaseTables.FeedCursors} (consumer, seq, updated_at) VALUES ($1, $2, now())
            ON CONFLICT (consumer) DO UPDATE SET seq = GREATEST({WincheDatabaseTables.FeedCursors}.seq, EXCLUDED.seq), updated_at = now()
            """;
        cmd.Parameters.AddWithValue(consumer);
        cmd.Parameters.AddWithValue(seq);
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
