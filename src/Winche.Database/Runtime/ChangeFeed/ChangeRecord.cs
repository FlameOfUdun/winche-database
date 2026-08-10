using Winche.Database.Documents;

namespace Winche.Database.Runtime.ChangeFeed;

public enum ChangeType { Added, Modified, Removed }

/// <summary>
/// One durable feed row (winche_documents_changes). <see cref="Xid"/> is the writing transaction's
/// id — together with <see cref="Seq"/> it forms the row's <see cref="ChangeCursor"/>; <see cref="Seq"/>
/// alone remains the intra-transaction order (still useful for logging/diagnostics) but is no longer
/// a safe cursor by itself — see ChangeFeedReader.
/// </summary>
public sealed record ChangeRecord(
    ulong Xid, long Seq, ChangeType Type, string Path, string Collection, long Version, DateTimeOffset CommitTime);

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
