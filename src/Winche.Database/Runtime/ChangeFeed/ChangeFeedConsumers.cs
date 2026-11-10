using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Winche.Database.Constants;

namespace Winche.Database.Runtime.ChangeFeed;

/// <summary>Where a durable consumer (a hook type) is: how far behind, whether it is failing, and its dead letters.</summary>
/// <param name="Name">The consumer's cursor name (for hooks, <c>hooks:</c> + the hook type's full name).</param>
/// <param name="Position">Its saved position; null before it first ran.</param>
/// <param name="OldestPendingAt">Commit time of the oldest change it hasn't delivered; null when it is caught up.</param>
/// <param name="Lag">How long that change has been waiting; zero when caught up.</param>
/// <param name="FailingSince">When it started failing on its current change; null when it isn't failing.</param>
/// <param name="FailingAttempts">How many times in a row it has failed on that change.</param>
/// <param name="LastError">The last failure's exception text.</param>
/// <param name="DeadLetters">Changes it gave up on that are waiting for replay or discard.</param>
public sealed record DurableConsumerStatus(
    string Name,
    FeedPosition? Position,
    DateTimeOffset? OldestPendingAt,
    TimeSpan Lag,
    DateTimeOffset? FailingSince,
    int FailingAttempts,
    string? LastError,
    long DeadLetters);

/// <summary>A change a durable consumer gave up on.</summary>
public sealed record DeadLetter(
    long Id,
    string Consumer,
    ChangeRecord Change,
    string? Error,
    int Attempts,
    DateTimeOffset FirstFailedAt,
    DateTimeOffset DeadLetteredAt);

/// <summary>The outcome of <see cref="ChangeFeedConsumers.ReplayDeadLettersAsync"/>.</summary>
/// <param name="Replayed">Delivered and removed from the dead letters.</param>
/// <param name="Failed">Failed again; kept, with the new error.</param>
public sealed record DeadLetterReplayResult(int Replayed, int Failed);

/// <summary>
/// Operations on the durable consumers (hooks): their status, and the changes they gave up on.
/// Registered by <c>AddWincheDatabase</c>; meant for health checks and admin endpoints.
/// </summary>
public sealed class ChangeFeedConsumers(
    [FromKeyedServices(WincheDatabaseKeys.DataSource)] NpgsqlDataSource source,
    ChangeFeedConsumerSet consumers)
{
    private readonly ChangeFeedReader _reader = new(source);

    /// <summary>The durable consumers' names.</summary>
    public IReadOnlyList<string> Names => consumers.DurableNames;

    /// <summary>The status of every durable consumer.</summary>
    public async Task<IReadOnlyList<DurableConsumerStatus>> GetStatusAsync(CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT n.name, c.seq, COALESCE(c.txid, '0')::text,
                   (SELECT x.commit_time FROM {WincheDatabaseTables.Changes} AS x
                    WHERE c.consumer IS NOT NULL
                      AND (x.txid, x.seq) > (COALESCE(c.txid, '0'), c.seq)
                      AND x.txid < pg_snapshot_xmin(pg_current_snapshot())
                    ORDER BY x.txid, x.seq LIMIT 1),
                   c.failing_since, COALESCE(c.failing_attempts, 0), c.failing_error,
                   (SELECT count(*) FROM {WincheDatabaseTables.DeadLetters} AS d WHERE d.consumer = n.name),
                   now()
            FROM unnest($1::text[]) WITH ORDINALITY AS n(name, i)
            LEFT JOIN {WincheDatabaseTables.FeedCursors} AS c ON c.consumer = n.name
            ORDER BY n.i
            """;
        cmd.Parameters.AddWithValue(consumers.DurableNames.ToArray());
        var statuses = new List<DurableConsumerStatus>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var oldest = reader.IsDBNull(3) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(3);
            var now = reader.GetFieldValue<DateTimeOffset>(8);
            statuses.Add(new DurableConsumerStatus(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : new FeedPosition(ulong.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture), reader.GetInt64(1)),
                oldest,
                oldest is { } o && now > o ? now - o : TimeSpan.Zero,
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetInt64(7)));
        }
        return statuses;
    }

    /// <summary>Dead letters, oldest change first; all consumers when <paramref name="consumer"/> is null.</summary>
    public Task<IReadOnlyList<DeadLetter>> GetDeadLettersAsync(string? consumer = null, int limit = 100, CancellationToken ct = default) =>
        ReadDeadLettersAsync(consumer, after: null, limit, ct);

    private async Task<IReadOnlyList<DeadLetter>> ReadDeadLettersAsync(string? consumer, FeedPosition? after, int limit, CancellationToken ct)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, consumer, seq, txid::text, type, document_path, collection_path, version, commit_time,
                   error, attempts, first_failed_at, dead_lettered_at
            FROM {WincheDatabaseTables.DeadLetters}
            WHERE ($1::text IS NULL OR consumer = $1)
              AND ($2::text IS NULL OR (txid, seq) > ($2::text::xid8, $3))
            ORDER BY txid, seq, id
            LIMIT $4
            """;
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)consumer ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = after is { } a ? ChangeFeedReader.Text(a.TxId) : DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(after?.Seq ?? 0L);
        cmd.Parameters.AddWithValue(limit);
        var letters = new List<DeadLetter>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var change = new ChangeRecord(
                reader.GetInt64(2),
                ChangeTypes.FromWire(reader.GetString(4)),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetInt64(7),
                reader.GetFieldValue<DateTimeOffset>(8))
            {
                TxId = ulong.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture),
            };
            letters.Add(new DeadLetter(
                reader.GetInt64(0),
                reader.GetString(1),
                change,
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetInt32(10),
                reader.GetFieldValue<DateTimeOffset>(11),
                reader.GetFieldValue<DateTimeOffset>(12)));
        }
        return letters;
    }

    /// <summary>
    /// Delivers <paramref name="consumer"/>'s dead letters again, oldest first, with each document's current
    /// state (as live delivery does). A delivered one is removed; one that fails again stays, with the new
    /// error. Runs on the calling node, alongside live delivery — hooks are idempotent, so that is safe.
    /// </summary>
    public async Task<DeadLetterReplayResult> ReplayDeadLettersAsync(string consumer, CancellationToken ct = default)
    {
        var target = consumers.Durable.FirstOrDefault(c => c.DurableName == consumer)
            ?? throw new ArgumentException($"No durable consumer is named '{consumer}'.", nameof(consumer));

        int replayed = 0, failed = 0;
        FeedPosition? after = null;
        while (true)
        {
            var page = await ReadDeadLettersAsync(consumer, after, 100, ct);
            if (page.Count == 0) break;

            foreach (var letter in page)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var records = new[] { letter.Change };
                    await target.OnBatchAsync(new ChangeBatch(records, await _reader.FetchDocumentsAsync(records, ct)), ct);
                    await ExecuteAsync($"DELETE FROM {WincheDatabaseTables.DeadLetters} WHERE id = $1", ct, letter.Id);
                    replayed++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var cause = ex is ChangeDeliveryException { InnerException: { } inner } ? inner : ex;
                    await ExecuteAsync(
                        $"UPDATE {WincheDatabaseTables.DeadLetters} SET attempts = attempts + 1, error = $2 WHERE id = $1",
                        ct, letter.Id, cause.ToString());
                    failed++;
                }
            }
            after = page[^1].Change.Position;
        }
        return new DeadLetterReplayResult(replayed, failed);
    }

    /// <summary>Drops a dead letter without delivering it. False when there is none with that id.</summary>
    public async Task<bool> DiscardDeadLetterAsync(long id, CancellationToken ct = default) =>
        await ExecuteAsync($"DELETE FROM {WincheDatabaseTables.DeadLetters} WHERE id = $1", ct, id) > 0;

    private async Task<int> ExecuteAsync(string sql, CancellationToken ct, params object[] args)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var arg in args) cmd.Parameters.AddWithValue(arg);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
