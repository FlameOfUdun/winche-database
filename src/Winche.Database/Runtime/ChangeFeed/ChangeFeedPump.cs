using Microsoft.Extensions.Logging;
using Npgsql;
using Winche.Database.Documents;
using Winche.Database.Models;

namespace Winche.Database.Runtime.ChangeFeed;

/// <summary>
/// One per node: LISTEN winche_changes as wake-up + poll fallback; reads feed rows after
/// the cursor in seq order; fetches changed docs once per batch; delivers sequentially to
/// consumers (at-least-once for the feed; a consumer exception is logged and skipped — no
/// redelivery — so consumers must self-heal). Cursor starts at MAX(seq) — live-only from boot.
/// </summary>
public sealed class ChangeFeedPump(
    NpgsqlDataSource source,
    string table,
    IReadOnlyList<IChangeFeedConsumer> consumers,
    ChangeFeedConfig config,
    ILogger<ChangeFeedPump> logger)
{
    private readonly ChangeFeedReader _reader = new(source, table);
    private long _cursor;

    public long Cursor => Interlocked.Read(ref _cursor);

    public async Task RunAsync(CancellationToken ct)
    {
        Interlocked.Exchange(ref _cursor, await _reader.GetMaxSeqAsync(ct));

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

            var batch = new ChangeBatch(records, await FetchDocsAsync(records, ct));
            foreach (var consumer in consumers)
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
        }
    }

    private async Task<IReadOnlyDictionary<string, Document>> FetchDocsAsync(
        IReadOnlyList<ChangeRecord> records, CancellationToken ct)
    {
        var paths = records.Where(r => r.Type != ChangeType.Removed)
            .Select(r => r.Path).Distinct(StringComparer.Ordinal).ToList();
        var docs = new Dictionary<string, Document>(StringComparer.Ordinal);
        if (paths.Count == 0) return docs;

        await using var conn = await source.OpenConnectionAsync(ct);
        var ops = new DocumentOperations(conn, null, table);
        foreach (var path in paths)
        {
            var doc = await ops.GetAsync(path, ct);
            if (doc is not null) docs[path] = doc;                // deleted-again docs stay absent (conservative)
        }
        return docs;
    }
}
