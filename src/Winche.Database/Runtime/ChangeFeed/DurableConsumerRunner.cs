using Microsoft.Extensions.Logging;
using Npgsql;
using Winche.Database.DependencyInjection;

namespace Winche.Database.Runtime.ChangeFeed;

/// <summary>
/// Delivery loop for ONE durable consumer (spec A): read after the persisted cursor →
/// deliver → persist AFTER success. A failed batch is redelivered one change at a time, so only the
/// failing change is retried (capped backoff 1s→30s) — true at-least-once; a change that keeps failing
/// for ChangeFeedConfig.DeadLetterAfter is moved to the dead-letter table and delivery carries on.
/// First run without a cursor row pins to MAX(seq) (no historical replay).
///
/// Every node runs a runner, but only the one holding the consumer's advisory lock delivers; the rest
/// retry every <see cref="ChangeFeedConfig.LeaderRetryInterval"/>. Without this, each node delivered
/// every change (hooks fired once per node, concurrently). A node that takes over re-reads the
/// persisted cursor, so delivery continues where the previous holder stopped.
/// </summary>
public sealed class DurableConsumerRunner(
    NpgsqlDataSource source,
    IChangeFeedConsumer consumer,
    ChangeFeedConfig config,
    ILogger logger)
{
    private readonly ChangeFeedReader _reader = new(source);
    private readonly string _name = consumer.DurableName
        ?? throw new ArgumentException("Runner requires a durable consumer.");

    public async Task RunAsync(Func<CancellationToken, Task> waitForWake, CancellationToken ct)
    {
        var lockName = $"durable-consumer:{_name}";
        while (!ct.IsCancellationRequested)
        {
            Winche.Core.AdvisoryLease? lease = null;
            try
            {
                lease = await Winche.Core.AdvisoryLease.TryAcquireAsync(source, lockName, config.LeaseHeartbeat, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Durable consumer '{Consumer}' could not try for its lock", _name);
            }

            if (lease is null)
            {
                try { await Task.Delay(config.LeaderRetryInterval, ct); } catch (OperationCanceledException) { break; }
                continue;
            }

            await using (lease)
            {
                logger.LogInformation("Durable consumer '{Consumer}' is active on this node", _name);
                using var active = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.Lost);
                await DeliverAsync(waitForWake, active.Token);
                if (!ct.IsCancellationRequested)
                    logger.LogWarning("Durable consumer '{Consumer}' lost its lock; standing by", _name);
            }
        }
    }

    /// <summary>
    /// Delivers until <paramref name="ct"/> trips (shutdown or a lost lock). When a batch fails, the changes
    /// in it are delivered one at a time, so the cursor moves past every change that succeeds and only the
    /// failing one is retried. A change that keeps failing for <see cref="ChangeFeedConfig.DeadLetterAfter"/>
    /// goes to the dead-letter table and delivery carries on.
    /// </summary>
    private async Task DeliverAsync(Func<CancellationToken, Task> waitForWake, CancellationToken ct)
    {
        FeedPosition? cursor = null;
        FeedPosition? oneAtATimeThrough = null;   // the end of the batch that failed
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            IReadOnlyList<ChangeRecord> records;
            ChangeBatch batch;
            try
            {
                // Bootstrap is INSIDE the retry loop so transient PG failures hit backoff/log,
                // not a silent unobserved crash of the runner task.
                cursor ??= await BootstrapAsync(ct);
                if (oneAtATimeThrough is { } through && cursor.Value.CompareTo(through) >= 0) oneAtATimeThrough = null;

                records = await _reader.ReadAfterAsync(cursor.Value, oneAtATimeThrough is null ? config.BatchSize : 1, ct);
                if (records.Count == 0)
                {
                    failures = 0;
                    oneAtATimeThrough = null;
                    try { await waitForWake(ct); } catch (OperationCanceledException) { break; }
                    continue;
                }
                batch = new ChangeBatch(records, await _reader.FetchDocumentsAsync(records, ct));
                if (consumer.WantsSnapshots)
                    batch = batch with { Snapshots = await _reader.FetchSnapshotsAsync(records, ct) };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                var backoff = Backoff(failures);
                logger.LogError(ex, "Durable consumer '{Consumer}' could not read the feed (attempt {Attempt}); retrying in {Backoff}",
                    _name, failures, backoff);
                try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { break; }
                continue;
            }

            try
            {
                await consumer.OnBatchAsync(batch, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception caught)
            {
                failures++;
                WincheDatabaseMetrics.HookFailures.Add(1, WincheDatabaseMetrics.Tags("consumer", _name));
                var ex = caught;
                ChangeRecord record;
                if (caught is ChangeDeliveryException { Delivered: var delivered } partial && delivered < records.Count)
                {
                    // The consumer said which change failed: keep the ones before it, retry only it.
                    ex = partial.InnerException ?? partial;
                    record = records[delivered];
                    if (delivered > 0)
                    {
                        cursor = records[delivered - 1].Position;
                        try { await _reader.SavePositionAsync(_name, cursor.Value, ct); }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                        catch (Exception persistEx)
                        {
                            logger.LogWarning(persistEx, "Durable consumer '{Consumer}' cursor persist failed; redelivery possible after restart", _name);
                        }
                    }
                }
                else if (records.Count > 1)
                {
                    // Don't redo the whole batch on every retry: go one change at a time to find the one that fails.
                    oneAtATimeThrough = records[^1].Position;
                    logger.LogWarning(caught, "Durable consumer '{Consumer}' failed on a batch of {Count} changes; delivering them one at a time",
                        _name, records.Count);
                    continue;
                }
                else
                {
                    record = records[0];
                }

                if (await TryDeadLetterAsync(record, ex, ct))
                {
                    cursor = record.Position;
                    failures = 0;
                    continue;
                }

                var backoff = Backoff(failures);
                logger.LogError(ex,
                    "Durable consumer '{Consumer}' failed on change {Seq} ({Type} {Path}), attempt {Attempt}; retrying in {Backoff}",
                    _name, record.Seq, record.Type, record.Path, failures, backoff);
                try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { break; }
                continue;
            }

            cursor = records[^1].Position;
            failures = 0;
            WincheDatabaseMetrics.HookChanges.Add(records.Count, WincheDatabaseMetrics.Tags("consumer", _name));
            WincheDatabaseMetrics.HookLag.Record(Math.Max(0, (DateTimeOffset.UtcNow - records[^1].CommitTime).TotalSeconds),
                WincheDatabaseMetrics.Tags("consumer", _name));
            // Persist cursor AFTER successful delivery; persist failures log a warning
            // but do NOT count as delivery failure — redelivery possible after restart.
            try
            {
                await _reader.SavePositionAsync(_name, cursor.Value, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception persistEx)
            {
                logger.LogWarning(persistEx,
                    "Durable consumer '{Consumer}' cursor persist failed; redelivery possible after restart",
                    _name);
            }
        }
    }

    /// <summary>
    /// Records the failure; once the change has been failing for <see cref="ChangeFeedConfig.DeadLetterAfter"/>,
    /// moves it to the dead-letter table (and the cursor past it) and returns true.
    /// </summary>
    private async Task<bool> TryDeadLetterAsync(ChangeRecord record, Exception error, CancellationToken ct)
    {
        try
        {
            var (attempts, failingFor) = await _reader.RecordFailureAsync(_name, record, error.ToString(), ct);
            if (config.DeadLetterAfter is not { } limit || failingFor < limit) return false;

            await _reader.DeadLetterAsync(_name, record, ct);
            WincheDatabaseMetrics.HookDeadLetters.Add(1, WincheDatabaseMetrics.Tags("consumer", _name));
            logger.LogError(error,
                "Durable consumer '{Consumer}' gave up on change {Seq} ({Type} {Path}) after {Attempts} attempts over {FailingFor}; " +
                "moved it to the dead letters (replay with ChangeFeedConsumers.ReplayDeadLettersAsync)",
                _name, record.Seq, record.Type, record.Path, attempts, failingFor);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;   // shutting down; the caller's delay sees the cancellation and stops
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Durable consumer '{Consumer}' could not record a failure", _name);
            return false;
        }
    }

    private static TimeSpan Backoff(int failures) =>
        TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(failures, 5) - 1)));

    private async Task<FeedPosition> BootstrapAsync(CancellationToken ct)
    {
        var cursor = await _reader.GetPositionAsync(_name, ct);
        if (cursor is not null) return cursor.Value;

        // A new consumer starts from now (no replay of history).
        var head = await _reader.GetHeadAsync(ct);
        await _reader.SavePositionAsync(_name, head, ct);
        return head;
    }

}
