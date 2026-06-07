using Microsoft.Extensions.Logging;
using Npgsql;
using Winche.Database.Documents;
using Winche.Database.DependencyInjection;

namespace Winche.Database.Runtime.ChangeFeed;

/// <summary>
/// One per node: LISTEN winche_changes as wake-up + poll fallback; reads feed rows after
/// the cursor in seq order; fetches changed docs once per batch; delivers sequentially to
/// ephemeral consumers (at-least-once for the feed; a consumer exception is logged and skipped —
/// no redelivery — so consumers must self-heal). Cursor starts at MAX(seq) — live-only from boot.
/// Durable consumers (DurableName != null) run in their own DurableConsumerRunner tasks that
/// share the same wake signal — they receive no-replay, at-least-once delivery with cursor
/// persistence and failure retry.
/// </summary>
public sealed class ChangeFeedPump(
    NpgsqlDataSource source,
    IReadOnlyList<IChangeFeedConsumer> consumers,
    ChangeFeedConfig config,
    ILogger<ChangeFeedPump> logger)
{
    private readonly ChangeFeedReader _reader = new(source);
    private readonly IReadOnlyList<IChangeFeedConsumer> _ephemeral = consumers.Where(c => c.DurableName is null).ToList();
    private readonly IReadOnlyList<IChangeFeedConsumer> _durable = ValidateDurableNames(consumers);
    private readonly List<SemaphoreSlim> _wakeSignals = [];
    private long _cursor;

    public long Cursor => Interlocked.Read(ref _cursor);

    public async Task RunAsync(CancellationToken ct)
    {
        Interlocked.Exchange(ref _cursor, await _reader.GetMaxSeqAsync(ct));

        // Spawn one DurableConsumerRunner per durable consumer, each with its own wake semaphore.
        var runnerTasks = new List<Task>();
        foreach (var durableConsumer in _durable)
        {
            var sem = new SemaphoreSlim(0, 1);
            _wakeSignals.Add(sem);
            var runner = new DurableConsumerRunner(source, durableConsumer, config, logger);
            // Use the timeout overload — avoids queuing an abandoned waiter on every poll
            // timeout (unbounded growth + wake-stealing with Task.WhenAny).
            // sem.WaitAsync(timeout, ct) returns bool (acquired/timeout) — ignore result;
            // avoids queuing an abandoned waiter on every poll timeout (the Task.WhenAny pattern
            // left dangling waiters that swallowed future Releases, causing unbounded growth).
            async Task WaitForWake(CancellationToken wCt) =>
                await sem.WaitAsync(config.PollInterval, wCt);
            runnerTasks.Add(Task.Run(() => runner.RunAsync(WaitForWake, ct), ct));
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await ListenLoopAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Change feed pump error; reconnecting…");
                    try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
                }
            }
        }
        finally
        {
            // Wait for all runner tasks to complete after the listen loop exits.
            if (runnerTasks.Count > 0)
                await Task.WhenAll(runnerTasks);
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        // Use a dedicated non-pooled connection for LISTEN so WaitAsync state never overlaps with pool connections.
        await using var conn = source.CreateConnection();
        await conn.OpenAsync(ct);
        conn.Notification += (_, _) => { };          // notifications surface by completing WaitAsync

        await using (var listen = conn.CreateCommand())
        {
            listen.CommandText = "LISTEN winche_changes";
            await listen.ExecuteNonQueryAsync(ct);
        }

        await DrainAsync(ct);                        // close the boot window

        var waitTask = conn.WaitAsync(ct);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var winner = await Task.WhenAny(waitTask, Task.Delay(config.PollInterval, pollCts.Token));
                pollCts.Cancel();                        // no timer buildup
                if (winner == waitTask)
                {
                    if (!ct.IsCancellationRequested) await waitTask;   // surface connection failures → reconnect; skip on clean shutdown
                    await DrainAsync(ct);                              // drain before re-entering WaitAsync
                    waitTask = conn.WaitAsync(ct);
                }
                else
                {
                    await DrainAsync(ct);                              // poll-interval fallback
                }
            }
        }
        finally
        {
            // Wait for any pending WaitAsync to settle before DisposeAsync closes the connection;
            // suppress cancellation/transient errors — the connection is closing anyway.
            try { await waitTask; } catch { }
        }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        while (true)
        {
            var records = await _reader.ReadAfterAsync(Cursor, config.BatchSize, ct);
            if (records.Count == 0) return;

            // Skip the doc fetch when there are no ephemeral consumers — durable runners
            // do their own fetching in DurableConsumerRunner (durable-only deployments).
            IReadOnlyDictionary<string, Document> docs = _ephemeral.Count > 0
                ? await FetchDocsAsync(records, ct)
                : new Dictionary<string, Document>(StringComparer.Ordinal);
            var batch = new ChangeBatch(records, docs);
            foreach (var consumer in _ephemeral)
            {
                try
                {
                    await consumer.OnBatchAsync(batch, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Change feed consumer {Consumer} failed for batch ending at seq {Seq}",
                        consumer.GetType().Name, records[^1].Seq);
                }
            }
            Interlocked.Exchange(ref _cursor, records[^1].Seq);

            // Wake all durable runners — they read their own cursors independently.
            foreach (var sem in _wakeSignals)
            {
                if (sem.CurrentCount == 0) sem.Release();
            }
        }
    }

    private async Task<IReadOnlyDictionary<string, Document>> FetchDocsAsync(
        IReadOnlyList<ChangeRecord> records, CancellationToken ct)
    {
        var paths = records.Where(r => r.Type != ChangeType.Removed)
            .Select(r => r.Path).Distinct(StringComparer.Ordinal).ToList();
        if (paths.Count == 0) return new Dictionary<string, Document>(StringComparer.Ordinal);

        await using var conn = await source.OpenConnectionAsync(ct);
        return await new DocumentOperations(conn, null).GetManyAsync(paths, ct);
    }

    /// <summary>
    /// Validates that no two durable consumers share the same DurableName, then returns
    /// the durable subset. Two consumers with the same name would silently corrupt each
    /// other's persisted cursor.
    /// </summary>
    private static IReadOnlyList<IChangeFeedConsumer> ValidateDurableNames(IReadOnlyList<IChangeFeedConsumer> consumers)
    {
        var durable = consumers.Where(c => c.DurableName is not null).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in durable)
        {
            if (!seen.Add(c.DurableName!))
                throw new ArgumentException(
                    $"Two durable consumers share the name '{c.DurableName}'; each durable consumer must have a unique DurableName.");
        }
        return durable;
    }
}
