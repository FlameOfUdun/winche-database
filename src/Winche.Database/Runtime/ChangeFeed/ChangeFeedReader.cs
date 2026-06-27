using Npgsql;
using Winche.Database.Constants;
using Winche.Database.Documents;

namespace Winche.Database.Runtime.ChangeFeed;

/// <summary>Reads the durable feed.</summary>
public sealed class ChangeFeedReader(NpgsqlDataSource source)
{
    /// <summary>
    /// Reads up to <paramref name="limit"/> change records with seq &gt; <paramref name="afterSeq"/>, ordered by seq.
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
    public async Task<IReadOnlyList<ChangeRecord>> ReadAfterAsync(long afterSeq, int limit, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT seq, type, document_path, collection_path, version, commit_time FROM {WincheTables.Changes} WHERE seq > $1 ORDER BY seq LIMIT $2";
        cmd.Parameters.AddWithValue(afterSeq);
        cmd.Parameters.AddWithValue(limit);

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
                reader.GetFieldValue<DateTimeOffset>(5)));
        }
        return records;
    }

    public async Task<long> GetMaxSeqAsync(CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COALESCE(MAX(seq), 0) FROM {WincheTables.Changes}";
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
        cmd.CommandText = $"SELECT EXISTS(SELECT 1 FROM {WincheTables.Changes} WHERE seq > $1 AND {column} = $2)";
        cmd.Parameters.AddWithValue(afterSeq);
        cmd.Parameters.AddWithValue(value);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Smallest seq still in the feed, or 0 when empty.</summary>
    public async Task<long> GetMinSeqAsync(CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COALESCE(MIN(seq), 0) FROM {WincheTables.Changes}";
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<long?> GetCursorAsync(string consumer, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT seq FROM {WincheTables.FeedCursors} WHERE consumer = $1";
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
            INSERT INTO {WincheTables.FeedCursors} (consumer, seq, updated_at) VALUES ($1, $2, now())
            ON CONFLICT (consumer) DO UPDATE SET seq = GREATEST({WincheTables.FeedCursors}.seq, EXCLUDED.seq), updated_at = now()
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
        cmd.CommandText = $"DELETE FROM {WincheTables.Changes} WHERE commit_time < $1";
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
