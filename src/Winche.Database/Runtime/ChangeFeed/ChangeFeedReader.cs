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

    private const string ClearFailure =
        "failing_txid = NULL, failing_seq = NULL, failing_since = NULL, failing_attempts = 0, failing_error = NULL";

    /// <summary>Saves a durable consumer's position and clears its failure state; never moves it backwards.</summary>
    public async Task SavePositionAsync(string consumer, FeedPosition position, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $$"""
            INSERT INTO {{WincheDatabaseTables.FeedCursors}} AS c (consumer, seq, txid, updated_at) VALUES ($1, $2, $3::text::xid8, now())
            ON CONFLICT (consumer) DO UPDATE SET seq = EXCLUDED.seq, txid = EXCLUDED.txid, updated_at = now(),
                {{ClearFailure}}
            WHERE (COALESCE(c.txid, '0'), c.seq) < (EXCLUDED.txid, EXCLUDED.seq)
            """;
        cmd.Parameters.AddWithValue(consumer);
        cmd.Parameters.AddWithValue(position.Seq);
        cmd.Parameters.AddWithValue(position.TxId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Records that <paramref name="consumer"/> failed on <paramref name="record"/> (the change right after its
    /// cursor). Returns how many times in a row it has failed on that change and for how long; a different
    /// change restarts the count. Kept on the cursor row, so the clock survives restarts and node changes.
    /// </summary>
    public async Task<(int Attempts, TimeSpan FailingFor)> RecordFailureAsync(
        string consumer, ChangeRecord record, string error, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE {WincheDatabaseTables.FeedCursors} SET
                failing_since    = CASE WHEN failing_txid = $2::text::xid8 AND failing_seq = $3 THEN failing_since ELSE now() END,
                failing_attempts = CASE WHEN failing_txid = $2::text::xid8 AND failing_seq = $3 THEN failing_attempts + 1 ELSE 1 END,
                failing_txid = $2::text::xid8, failing_seq = $3, failing_error = $4
            WHERE consumer = $1
            RETURNING failing_attempts, EXTRACT(EPOCH FROM now() - failing_since)::float8
            """;
        cmd.Parameters.AddWithValue(consumer);
        cmd.Parameters.AddWithValue(Text(record.TxId));
        cmd.Parameters.AddWithValue(record.Seq);
        cmd.Parameters.AddWithValue(Truncate(error));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return (1, TimeSpan.Zero);
        return (reader.GetInt32(0), TimeSpan.FromSeconds(reader.GetDouble(1)));
    }

    /// <summary>
    /// Gives up on <paramref name="record"/> for <paramref name="consumer"/>: copies it, with the recorded
    /// failure, to the dead-letter table and moves the cursor past it, in one statement.
    /// </summary>
    public async Task DeadLetterAsync(string consumer, ChangeRecord record, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $$"""
            WITH failure AS (
                SELECT failing_error, failing_attempts, failing_since FROM {{WincheDatabaseTables.FeedCursors}} WHERE consumer = $1
            ), dead AS (
                INSERT INTO {{WincheDatabaseTables.DeadLetters}}
                    (consumer, seq, txid, type, document_path, collection_path, version, commit_time, error, attempts, first_failed_at)
                VALUES ($1, $2, $3::text::xid8, $4, $5, $6, $7, $8,
                        (SELECT failing_error FROM failure),
                        COALESCE((SELECT failing_attempts FROM failure), 0),
                        COALESCE((SELECT failing_since FROM failure), now()))
                ON CONFLICT (consumer, txid, seq) DO NOTHING
            )
            UPDATE {{WincheDatabaseTables.FeedCursors}} AS c SET seq = $2, txid = $3::text::xid8, updated_at = now(), {{ClearFailure}}
            WHERE c.consumer = $1 AND (COALESCE(c.txid, '0'), c.seq) < ($3::text::xid8, $2)
            """;
        cmd.Parameters.AddWithValue(consumer);
        cmd.Parameters.AddWithValue(record.Seq);
        cmd.Parameters.AddWithValue(Text(record.TxId));
        cmd.Parameters.AddWithValue(ChangeTypes.ToWire(record.Type));
        cmd.Parameters.AddWithValue(record.Path);
        cmd.Parameters.AddWithValue(record.Collection);
        cmd.Parameters.AddWithValue(record.Version);
        cmd.Parameters.AddWithValue(record.CommitTime);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Gives each of <paramref name="consumers"/> that has no cursor yet a copy of <paramref name="from"/>'s
    /// cursor, so consumers split out of an older one carry on where it was — but only while no cursor
    /// starting with <paramref name="familyPrefix"/> exists, i.e. on the first upgrade. After that a new
    /// consumer in the family starts from now, like any new consumer. Returns the cursors created.
    /// </summary>
    public async Task<int> InheritCursorAsync(string from, string familyPrefix, IReadOnlyList<string> consumers, CancellationToken ct = default)
    {
        if (consumers.Count == 0) return 0;
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {WincheDatabaseTables.FeedCursors} (consumer, seq, txid, updated_at)
            SELECT n.name, old.seq, COALESCE(old.txid, '0'), now()
            FROM unnest($3::text[]) AS n(name)
            CROSS JOIN {WincheDatabaseTables.FeedCursors} AS old
            WHERE old.consumer = $1
              AND NOT EXISTS (SELECT 1 FROM {WincheDatabaseTables.FeedCursors} WHERE starts_with(consumer, $2))
            ON CONFLICT (consumer) DO NOTHING
            """;
        cmd.Parameters.AddWithValue(from);
        cmd.Parameters.AddWithValue(familyPrefix);
        cmd.Parameters.AddWithValue(consumers.ToArray());
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Retention that never drops a change a durable consumer still needs: deletes rows older than
    /// <paramref name="cutoff"/> that every one of <paramref name="consumers"/> has delivered, and rows older
    /// than <paramref name="hardCutoff"/> regardless. Returns the rows deleted and, of those, how many some
    /// consumer had not delivered (changes it will never see).
    /// </summary>
    public async Task<(int Pruned, int Undelivered)> PruneAsync(
        DateTimeOffset cutoff, DateTimeOffset hardCutoff, IReadOnlyList<string> consumers, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH slowest AS (
                SELECT COALESCE(txid, '0') AS txid, seq FROM {WincheDatabaseTables.FeedCursors}
                WHERE consumer = ANY($3) ORDER BY 1, 2 LIMIT 1
            ), pruned AS (
                DELETE FROM {WincheDatabaseTables.Changes} AS c
                WHERE c.commit_time < $1
                  AND (c.commit_time < $2
                       OR NOT EXISTS (SELECT 1 FROM slowest)
                       OR (c.txid, c.seq) <= (SELECT txid, seq FROM slowest))
                RETURNING c.txid, EXISTS (SELECT 1 FROM slowest) AND (c.txid, c.seq) > (SELECT txid, seq FROM slowest) AS undelivered
            ), horizon AS ({RecordHorizon("pruned")})
            SELECT count(*)::int, (count(*) FILTER (WHERE undelivered))::int FROM pruned
            """;
        cmd.Parameters.AddWithValue(cutoff);
        cmd.Parameters.AddWithValue(hardCutoff);
        cmd.Parameters.AddWithValue(consumers.ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    // Raises the pruned-through mark to the newest transaction among the deleted rows (a CTE over them).
    // ORDER BY … LIMIT 1 rather than max(): max(xid8) isn't available on every supported Postgres version.
    private static string RecordHorizon(string deleted) => $"""
        INSERT INTO {WincheDatabaseTables.FeedPruned} AS p (id, txid)
        SELECT 1, (SELECT txid FROM {deleted} ORDER BY txid DESC LIMIT 1) WHERE EXISTS (SELECT 1 FROM {deleted})
        ON CONFLICT (id) DO UPDATE SET txid = GREATEST(p.txid, EXCLUDED.txid)
        """;

    internal static string Text(ulong txid) => txid.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Truncate(string error) => error.Length <= 8000 ? error : error[..8000];

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
                ChangeTypes.FromWire(reader.GetString(1)),
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

    /// <summary>True when any committed feed row from a transaction after <paramref name="throughTxId"/> touches the collection.</summary>
    public Task<bool> HasCollectionChangesAfterAsync(ulong throughTxId, string collection, CancellationToken ct = default) =>
        ExistsAfterTxAsync(throughTxId, "collection_path", collection, ct);

    /// <summary>True when any committed feed row from a transaction after <paramref name="throughTxId"/> touches the document.</summary>
    public Task<bool> HasDocumentChangesAfterAsync(ulong throughTxId, string documentPath, CancellationToken ct = default) =>
        ExistsAfterTxAsync(throughTxId, "document_path", documentPath, ct);

    // column MUST be a trusted constant (never user input) — it is interpolated into the SQL text.
    private async Task<bool> ExistsAfterTxAsync(ulong throughTxId, string column, string value, CancellationToken ct)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT EXISTS(SELECT 1 FROM {WincheDatabaseTables.Changes} WHERE txid > $1::text::xid8 AND {column} = $2)";
        cmd.Parameters.AddWithValue(Text(throughTxId));
        cmd.Parameters.AddWithValue(value);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// True when no feed row after <paramref name="throughTxId"/> can have been pruned, so the feed can say
    /// whether anything changed since: the newest pruned transaction is at or before it.
    /// </summary>
    public async Task<bool> RetainsChangesAfterAsync(ulong throughTxId, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COALESCE((SELECT txid FROM {WincheDatabaseTables.FeedPruned} WHERE id = 1) <= $1::text::xid8, true)";
        cmd.Parameters.AddWithValue(Text(throughTxId));
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

    /// <summary>
    /// Deletes rows with commit_time older than the cutoff, delivered or not. Returns rows deleted.
    /// The pruner uses <see cref="PruneAsync"/>, which keeps rows a durable consumer still needs.
    /// </summary>
    public async Task<int> PruneBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            WITH pruned AS (DELETE FROM {WincheDatabaseTables.Changes} WHERE commit_time < $1 RETURNING txid),
            horizon AS ({RecordHorizon("pruned")})
            SELECT count(*)::int FROM pruned
            """;
        cmd.Parameters.AddWithValue(cutoff);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
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
