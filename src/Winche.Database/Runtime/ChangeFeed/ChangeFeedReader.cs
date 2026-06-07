using Npgsql;

namespace Winche.Database.Runtime.ChangeFeed;

/// <summary>Reads the durable feed. Identifiers from config only; every value is a parameter.</summary>
public sealed class ChangeFeedReader(NpgsqlDataSource source, string table)
{
    public async Task<IReadOnlyList<ChangeRecord>> ReadAfterAsync(long afterSeq, int limit, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT seq, type, path, collection, version, commit_time FROM {table}_changes WHERE seq > $1 ORDER BY seq LIMIT $2";
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
        cmd.CommandText = $"SELECT COALESCE(MAX(seq), 0) FROM {table}_changes";
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>True when any feed row after <paramref name="afterSeq"/> touches the collection.</summary>
    public async Task<bool> HasChangesAfterAsync(long afterSeq, string collection, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT EXISTS(SELECT 1 FROM {table}_changes WHERE seq > $1 AND collection = $2)";
        cmd.Parameters.AddWithValue(afterSeq);
        cmd.Parameters.AddWithValue(collection);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Smallest seq still in the feed, or 0 when empty.</summary>
    public async Task<long> GetMinSeqAsync(CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COALESCE(MIN(seq), 0) FROM {table}_changes";
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Retention: deletes rows with commit_time older than the cutoff. Returns rows deleted.</summary>
    public async Task<int> PruneBeforeAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {table}_changes WHERE commit_time < $1";
        cmd.Parameters.AddWithValue(cutoff);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
