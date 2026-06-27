using Microsoft.Extensions.Logging;
using Npgsql;
using Winche.Database.DependencyInjection;

namespace Winche.Database.Runtime.ChangeFeed;

/// <summary>
/// Delivery loop for ONE durable consumer (spec A): read after the persisted cursor →
/// deliver → persist AFTER success. Failure retries the SAME batch with capped backoff
/// (1s→30s) — true at-least-once; a poison batch blocks only this consumer (logged each retry).
/// First run without a cursor row pins to MAX(seq) (no historical replay).
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
        long? cursor = null;
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Bootstrap is INSIDE the retry loop so transient PG failures hit backoff/log,
                // not a silent unobserved crash of the runner task.
                cursor ??= await BootstrapAsync(ct);

                var records = await _reader.ReadAfterAsync(cursor.Value, config.BatchSize, ct);
                if (records.Count == 0)
                {
                    failures = 0;
                    try { await waitForWake(ct); } catch (OperationCanceledException) { break; }
                    continue;
                }

                var batch = new ChangeBatch(records, await _reader.FetchDocumentsAsync(records, ct));
                await consumer.OnBatchAsync(batch, ct);                 // throws → retry same batch

                cursor = records[^1].Seq;
                // Persist cursor AFTER successful delivery; persist failures log a warning
                // but do NOT count as delivery failure — redelivery possible after restart.
                try
                {
                    await _reader.SaveCursorAsync(_name, cursor.Value, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception persistEx)
                {
                    logger.LogWarning(persistEx,
                        "Durable consumer '{Consumer}' cursor persist failed; redelivery possible after restart",
                        _name);
                }
                failures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                var backoff = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(failures, 5) - 1)));
                logger.LogError(ex,
                    "Durable consumer '{Consumer}' failed (attempt {Attempt}); retrying same batch in {Backoff}",
                    _name, failures, backoff);
                try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task<long> BootstrapAsync(CancellationToken ct)
    {
        var cursor = await _reader.GetCursorAsync(_name, ct);
        if (cursor is not null) return cursor.Value;

        var max = await _reader.GetMaxSeqAsync(ct);
        await _reader.SaveCursorAsync(_name, max, ct);
        return max;
    }

}
