using System.Globalization;
using System.Text.Json.Nodes;
using Npgsql;
using Winche.Database.Constants;
using Winche.Database.DependencyInjection;

namespace Winche.Database.Runtime.Transactions;

/// <summary>
/// Transactions kept in Postgres (<see cref="TransactionStore.Database"/>), so every node can serve every step of
/// one — begin on one, read on another, commit on a third — with no sticky routing. The read set is a jsonb map
/// path → updateTime (JSON null: the document didn't exist). Expiry uses the database clock, so nodes' clocks
/// don't have to agree.
/// </summary>
internal sealed class PostgresTransactionLedger(NpgsqlDataSource source, TransactionConfig config) : ITransactionLedger
{
    private const string Table = WincheDatabaseTables.Transactions;

    private string Expired(string alias = "") =>
        $"(now() - {alias}last_activity > {Interval(config.IdleTimeoutSpan)} OR now() - {alias}created_at > {Interval(config.TotalTimeoutSpan)})";

    private static string Interval(TimeSpan t) =>
        $"interval '{t.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)} milliseconds'";

    public async Task<string> BeginAsync(CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        await using var cmd = source.CreateCommand($"INSERT INTO {Table} (id) VALUES ($1)");
        cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync(ct);
        return id;
    }

    public async Task RecordReadAsync(string id, string path, DateTimeOffset? updateTime, CancellationToken ct)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        string? recorded;
        bool known, expired;
        await using (var read = new NpgsqlCommand($"SELECT reads ? $2, reads ->> $2, {Expired()} FROM {Table} WHERE id = $1 FOR UPDATE", conn, tx))
        {
            read.Parameters.AddWithValue(id);
            read.Parameters.AddWithValue(path);
            await using var r = await read.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new TransactionAbortedException($"Transaction '{id}' is not active.");
            known = r.GetBoolean(0);
            recorded = r.IsDBNull(1) ? null : r.GetString(1);
            expired = r.GetBoolean(2);
        }

        if (expired)
        {
            await DeleteAsync(conn, tx, id, ct);
            await tx.CommitAsync(ct);
            throw new TransactionAbortedException($"Transaction '{id}' has expired.");
        }
        var value = updateTime?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        if (known)
        {
            if (!SameVersion(recorded, updateTime))
            {
                await DeleteAsync(conn, tx, id, ct);
                await tx.CommitAsync(ct);
                throw new TransactionAbortedException($"Document '{path}' changed during the transaction.");
            }
            await using var touch = new NpgsqlCommand($"UPDATE {Table} SET last_activity = now() WHERE id = $1", conn, tx);
            touch.Parameters.AddWithValue(id);
            await touch.ExecuteNonQueryAsync(ct);
        }
        else
        {
            await using var add = new NpgsqlCommand(
                $"UPDATE {Table} SET reads = reads || jsonb_build_object($2::text, $3::jsonb), last_activity = now() WHERE id = $1", conn, tx);
            add.Parameters.AddWithValue(id);
            add.Parameters.AddWithValue(path);
            add.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb, Value = value is null ? "null" : JsonValue.Create(value).ToJsonString() });
            await add.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private static bool SameVersion(string? recorded, DateTimeOffset? now) =>
        recorded is null ? now is null
            : now is not null && DateTimeOffset.Parse(recorded, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) == now.Value;

    public async Task<IReadOnlyDictionary<string, DateTimeOffset?>> ConsumeAsync(string id, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand($"DELETE FROM {Table} WHERE id = $1 RETURNING reads::text, {Expired()}");
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new TransactionAbortedException($"Transaction '{id}' is not active.");
        if (r.GetBoolean(1)) throw new TransactionAbortedException($"Transaction '{id}' has expired.");
        var reads = new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal);
        foreach (var (path, value) in JsonNode.Parse(r.GetString(0))!.AsObject())
            reads[path] = value is null ? null : DateTimeOffset.Parse(value.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return reads;
    }

    public async Task RollbackAsync(string id, CancellationToken ct)
    {
        await using var cmd = source.CreateCommand($"DELETE FROM {Table} WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> RemoveExpiredAsync(CancellationToken ct)
    {
        await using var cmd = source.CreateCommand($"DELETE FROM {Table} WHERE {Expired()}");
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> CountAsync(CancellationToken ct)
    {
        await using var cmd = source.CreateCommand($"SELECT count(*)::int FROM {Table}");
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task DeleteAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string id, CancellationToken ct)
    {
        await using var del = new NpgsqlCommand($"DELETE FROM {Table} WHERE id = $1", conn, tx);
        del.Parameters.AddWithValue(id);
        await del.ExecuteNonQueryAsync(ct);
    }
}
