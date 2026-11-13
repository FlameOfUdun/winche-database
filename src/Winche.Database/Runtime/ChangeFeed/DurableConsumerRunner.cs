using Microsoft.Extensions.Logging;
using Npgsql;
using Winche.Database.DependencyInjection;

namespace Winche.Database.Runtime.ChangeFeed;

/// <summary>
/// Runs ONE durable consumer of the database's changes on Winche.Core's feed runner
/// (<see cref="WincheFeedRunner{TRecord}"/>): one node at a time, at least once, in commit order, resuming from its
/// saved position; a change that keeps failing for <see cref="ChangeFeedConfig.DeadLetterAfter"/> becomes a dead letter.
/// What's database-specific is the batch: each delivery carries the changed documents (and, for consumers that want
/// them, the before/after snapshots).
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

    public Task RunAsync(Func<CancellationToken, Task> waitForWake, CancellationToken ct) =>
        new WincheFeedRunner<ChangeRecord>(_reader.Feed, _name, DeliverAsync, Options(config), logger).RunAsync(waitForWake, ct);

    internal static WincheFeedOptions Options(ChangeFeedConfig config) => new()
    {
        BatchSize = config.BatchSize,
        LeaseHeartbeat = config.LeaseHeartbeat,
        LeaderRetryInterval = config.LeaderRetryInterval,
        DeadLetterAfter = config.DeadLetterAfter,
    };

    /// <summary>Delivers <paramref name="records"/> with their documents to the consumer (also used to replay dead letters).</summary>
    internal async Task DeliverAsync(IReadOnlyList<ChangeRecord> records, CancellationToken ct)
    {
        var batch = new ChangeBatch(records, await _reader.FetchDocumentsAsync(records, ct));
        if (consumer.WantsSnapshots)
            batch = batch with { Snapshots = await _reader.FetchSnapshotsAsync(records, ct) };
        try
        {
            await consumer.OnBatchAsync(batch, ct);
        }
        catch (ChangeDeliveryException partial)
        {
            throw new FeedDeliveryException(partial.Delivered, partial.InnerException ?? partial);
        }
    }
}
