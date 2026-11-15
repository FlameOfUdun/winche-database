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
    ChangeFeedConsumerSet consumers) : IWincheFeedConsumers
{
    private readonly ChangeFeedReader _reader = new(source);

    /// <summary>The durable consumers' names.</summary>
    public IReadOnlyList<string> Names => consumers.DurableNames;
    /// <summary>How each durable consumer is doing (lag behind the oldest undelivered change, failure, dead letters).</summary>
    public async Task<IReadOnlyList<DurableConsumerStatus>> GetStatusAsync(CancellationToken ct = default) =>
        [.. (await _reader.Feed.GetStatusAsync(consumers.DurableNames, ct)).Select(s => new DurableConsumerStatus(
            s.Name, s.Position, s.OldestPendingAt, s.Lag, s.FailingSince, s.FailingAttempts, s.LastError, s.DeadLetters))];

    /// <summary>Changes durable consumers gave up on (of <paramref name="consumer"/>, if given), oldest first.</summary>
    public async Task<IReadOnlyList<DeadLetter>> GetDeadLettersAsync(string? consumer = null, int limit = 100, CancellationToken ct = default) =>
        [.. (await _reader.Feed.GetDeadLettersAsync(consumer, null, limit, ct)).Select(l => new DeadLetter(
            l.Id, l.Consumer, _reader.Feed.RecordOf(l), l.Error, l.Attempts, l.FirstFailedAt, l.DeadLetteredAt))];

    /// <summary>
    /// Delivers <paramref name="consumer"/>'s dead letters again, oldest first, with each document's current
    /// state (as live delivery does). A delivered one is removed; one that fails again stays, with the new
    /// error. Runs on the calling node, alongside live delivery — hooks are idempotent, so that is safe.
    /// </summary>
    public async Task<DeadLetterReplayResult> ReplayDeadLettersAsync(string consumer, CancellationToken ct = default)
    {
        var target = consumers.Durable.FirstOrDefault(c => c.DurableName == consumer)
            ?? throw new ArgumentException($"No durable consumer is named '{consumer}'.", nameof(consumer));
        var runner = new DurableConsumerRunner(source, target, new(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var result = await _reader.Feed.ReplayDeadLettersAsync(consumer, (record, t) => runner.DeliverAsync([record], t), ct);
        return new DeadLetterReplayResult(result.Replayed, result.Failed);
    }

    /// <summary>Drops a dead letter without delivering it. False when there is none with that id.</summary>
    public async Task<bool> DiscardDeadLetterAsync(long id, CancellationToken ct = default) =>
        await _reader.Feed.DeleteDeadLettersAsync(id, ct: ct) > 0;

    // ── as one of the console's feeds ──────────────────────────────────────────────

    string IWincheFeedConsumers.Feed => DatabaseFeed.Name;

    Task<IReadOnlyList<FeedConsumerStatus>> IWincheFeedConsumers.ConsumersAsync(CancellationToken ct) =>
        _reader.Feed.GetStatusAsync(consumers.DurableNames, ct);

    Task<IReadOnlyList<FeedDeadLetter>> IWincheFeedConsumers.DeadLettersAsync(string? consumer, int limit, CancellationToken ct) =>
        _reader.Feed.GetDeadLettersAsync(consumer, null, limit, ct);

    async Task<FeedReplayResult> IWincheFeedConsumers.RetryAsync(string consumer, CancellationToken ct)
    {
        var result = await ReplayDeadLettersAsync(consumer, ct);
        return new FeedReplayResult(result.Replayed, result.Failed);
    }

    Task<bool> IWincheFeedConsumers.DiscardAsync(long id, CancellationToken ct) => DiscardDeadLetterAsync(id, ct);
}
