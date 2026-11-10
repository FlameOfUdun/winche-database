using Winche.Database.Documents;

namespace Winche.Database.Runtime.ChangeFeed;

public enum ChangeType { Added, Modified, Removed }

/// <summary>One durable feed row (winche_changes).</summary>
public sealed record ChangeRecord(
    long Seq, ChangeType Type, string Path, string Collection, long Version, DateTimeOffset CommitTime)
{
    /// <summary>The writing transaction (Postgres xid8); 0 for rows from before the feed recorded it.</summary>
    public ulong TxId { get; init; }

    /// <summary>This record's place in the feed.</summary>
    public FeedPosition Position => new(TxId, Seq);
}

/// <summary>
/// A place in the feed: records are read in (<see cref="TxId"/>, <see cref="Seq"/>) order, and a position means
/// "everything at or before this has been delivered".
/// </summary>
public readonly record struct FeedPosition(ulong TxId, long Seq) : IComparable<FeedPosition>
{
    public int CompareTo(FeedPosition other) =>
        TxId != other.TxId ? TxId.CompareTo(other.TxId) : Seq.CompareTo(other.Seq);
}

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
