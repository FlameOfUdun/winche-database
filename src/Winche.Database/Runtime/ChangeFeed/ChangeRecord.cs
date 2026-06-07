using Winche.Database.Documents;

namespace Winche.Database.Runtime.ChangeFeed;

public enum ChangeType { Added, Modified, Removed }

/// <summary>One durable feed row (winche_changes).</summary>
public sealed record ChangeRecord(
    long Seq, ChangeType Type, string Path, string Collection, long Version, DateTimeOffset CommitTime);

/// <summary>
/// A pump delivery: ordered records + the still-existing documents for added/modified paths,
/// fetched ONCE per batch and shared by all consumers (spec §4).
/// </summary>
public sealed record ChangeBatch(
    IReadOnlyList<ChangeRecord> Records,
    IReadOnlyDictionary<string, Document> Documents);

public interface IChangeFeedConsumer
{
    /// <summary>
    /// Non-null → durable consumer: a cursor row is persisted per this name; the pump
    /// creates a <see cref="DurableConsumerRunner"/> for it. Null → ephemeral (current behaviour).
    /// </summary>
    string? DurableName => null;

    Task OnBatchAsync(ChangeBatch batch, CancellationToken ct);
}
