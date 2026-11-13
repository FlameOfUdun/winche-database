using Npgsql;
using Winche.Database.Constants;
using Winche.Database.Documents;

namespace Winche.Database.Runtime.ChangeFeed;

/// <summary>Reads the durable feed.</summary>
public sealed class ChangeFeedReader(NpgsqlDataSource source)
{
    /// <summary>
    /// The change table as a Winche.Core feed: durable consumers' cursors, failures and dead letters are kept there,
    /// in the tables every package's feed shares.
    /// </summary>
    internal WincheFeed<ChangeRecord> Feed { get; } = DatabaseFeed.Open(source);

    /// <summary>Up to <paramref name="limit"/> records after <paramref name="after"/>, of committed transactions only.</summary>
    public Task<IReadOnlyList<ChangeRecord>> ReadAfterAsync(FeedPosition after, int limit, CancellationToken ct = default) =>
        Feed.ReadAfterAsync(after, limit, ct);

    /// <summary>Where the feed is now: past every committed record, before any still to commit.</summary>
    public Task<FeedPosition> GetHeadAsync(CancellationToken ct = default) => Feed.GetHeadAsync(ct);

    /// <summary>Where <paramref name="consumer"/> has delivered up to, or null if it never ran.</summary>
    public Task<FeedPosition?> GetPositionAsync(string consumer, CancellationToken ct = default) => Feed.GetPositionAsync(consumer, ct);

    /// <summary>Moves <paramref name="consumer"/> forward to <paramref name="position"/> (never back) and clears its failure.</summary>
    public Task SavePositionAsync(string consumer, FeedPosition position, CancellationToken ct = default) =>
        Feed.SavePositionAsync(consumer, position, ct);

    /// <summary>Notes a failure on <paramref name="record"/>: attempts so far, and how long it has been failing.</summary>
    public Task<(int Attempts, TimeSpan FailingFor)> RecordFailureAsync(string consumer, ChangeRecord record, string error, CancellationToken ct = default) =>
        Feed.RecordFailureAsync(consumer, record, error, ct);

    /// <summary>Moves <paramref name="record"/> to the dead letters and <paramref name="consumer"/> past it.</summary>
    public Task DeadLetterAsync(string consumer, ChangeRecord record, CancellationToken ct = default) =>
        Feed.DeadLetterAsync(consumer, record, ct);

    /// <summary>Gives each of <paramref name="consumers"/> the cursor of <paramref name="from"/> (see <see cref="WincheFeed{TRecord}.InheritCursorAsync"/>).</summary>
    public Task<int> InheritCursorAsync(string from, string familyPrefix, IReadOnlyList<string> consumers, CancellationToken ct = default) =>
        Feed.InheritCursorAsync(from, familyPrefix, consumers, ct);


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
                SELECT COALESCE(txid, '0') AS txid, seq FROM {WincheFeedTables.Cursors}
                WHERE feed = '{DatabaseFeed.Name}' AND consumer = ANY($3) ORDER BY 1, 2 LIMIT 1
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

    /// <summary>True when any committed feed row from a transaction after <paramref name="throughTxId"/> touches a collection with this id.</summary>
    public async Task<bool> HasCollectionGroupChangesAfterAsync(ulong throughTxId, string collectionId, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        // The feed keeps the collection path; its id is the last segment.
        cmd.CommandText = $"SELECT EXISTS(SELECT 1 FROM {WincheDatabaseTables.Changes} WHERE txid > $1::text::xid8 AND (collection_path = $2 OR right(collection_path, $3) = $4))";
        cmd.Parameters.AddWithValue(Text(throughTxId));
        cmd.Parameters.AddWithValue(collectionId);
        cmd.Parameters.AddWithValue(collectionId.Length + 1);
        cmd.Parameters.AddWithValue("/" + collectionId);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

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

    /// <summary>The before/after snapshots stored with <paramref name="records"/> (only those that have any).</summary>
    public async Task<IReadOnlyDictionary<long, (Document? Before, Document? After)>> FetchSnapshotsAsync(
        IReadOnlyList<ChangeRecord> records, CancellationToken ct = default)
    {
        var result = new Dictionary<long, (Document? Before, Document? After)>();
        if (records.Count == 0) return result;
        var paths = records.ToDictionary(r => r.Seq, r => r.Path);
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT seq, before::text, after::text FROM {WincheDatabaseTables.Changes} WHERE seq = ANY($1) AND (before IS NOT NULL OR after IS NOT NULL)";
        cmd.Parameters.AddWithValue(paths.Keys.ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var seq = reader.GetInt64(0);
            var path = paths[seq];
            result[seq] = (
                reader.IsDBNull(1) ? null : ChangeSnapshots.Decode(path, reader.GetString(1)),
                reader.IsDBNull(2) ? null : ChangeSnapshots.Decode(path, reader.GetString(2)));
        }
        return result;
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
