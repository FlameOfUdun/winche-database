using Winche.Database.Documents;

namespace Winche.Database.Runtime.ChangeFeed;

public enum ChangeType { Added, Modified, Removed }

/// <summary>The feed's stored spelling of <see cref="ChangeType"/>.</summary>
internal static class ChangeTypes
{
    public static string ToWire(ChangeType type) => type switch
    {
        ChangeType.Added => "added",
        ChangeType.Modified => "modified",
        ChangeType.Removed => "removed",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static ChangeType FromWire(string type) => type switch
    {
        "added" => ChangeType.Added,
        "modified" => ChangeType.Modified,
        "removed" => ChangeType.Removed,
        _ => throw new InvalidOperationException($"Unknown change type '{type}'"),
    };
}

/// <summary>One durable feed row (winche_changes).</summary>
public sealed record ChangeRecord(
    long Seq, ChangeType Type, string Path, string Collection, long Version, DateTimeOffset CommitTime) : IFeedRecord
{
    /// <summary>The writing transaction (Postgres xid8); 0 for rows from before the feed recorded it.</summary>
    public ulong TxId { get; init; }

    /// <summary>This record's place in the feed (<see cref="Winche.Core.Feeds.FeedPosition"/>).</summary>
    public FeedPosition Position => new(TxId, Seq);

    DateTimeOffset IFeedRecord.At => CommitTime;

    public string Describe() => $"change {Seq} ({ChangeTypes.ToWire(Type)} {Path})";
}

/// <summary>The database's change table (<c>winche_changes</c>) as a Winche.Core feed, named <see cref="Name"/>.</summary>
internal static class DatabaseFeed
{
    public const string Name = "database";

    public static WincheFeed<ChangeRecord> Open(Npgsql.NpgsqlDataSource db) => new(
        db, Name, Constants.WincheDatabaseTables.Changes,
        "seq, type, document_path, collection_path, version, commit_time, txid::text", "commit_time",
        r => new ChangeRecord(r.GetInt64(0), ChangeTypes.FromWire(r.GetString(1)), r.GetString(2), r.GetString(3), r.GetInt64(4),
            r.GetFieldValue<DateTimeOffset>(5)) { TxId = ulong.Parse(r.GetString(6), System.Globalization.CultureInfo.InvariantCulture) },
        c => new System.Text.Json.Nodes.JsonObject
        {
            ["seq"] = c.Seq, ["txid"] = c.TxId.ToString(System.Globalization.CultureInfo.InvariantCulture), ["type"] = ChangeTypes.ToWire(c.Type),
            ["path"] = c.Path, ["collection"] = c.Collection, ["version"] = c.Version, ["commitTime"] = c.CommitTime.ToString("O"),
        },
        j => new ChangeRecord((long)j["seq"]!, ChangeTypes.FromWire((string)j["type"]!), (string)j["path"]!, (string)j["collection"]!,
            (long)j["version"]!, DateTimeOffset.Parse((string)j["commitTime"]!, System.Globalization.CultureInfo.InvariantCulture))
        { TxId = ulong.Parse((string)j["txid"]!, System.Globalization.CultureInfo.InvariantCulture) });
}

/// <summary>
/// A pump delivery: ordered records + the still-existing documents for added/modified paths,
/// fetched ONCE per batch and shared by all consumers (spec §4).
/// </summary>
public sealed record ChangeBatch(
    IReadOnlyList<ChangeRecord> Records,
    IReadOnlyDictionary<string, Document> Documents)
{
    /// <summary>
    /// True when the batch is everything the feed had up to its last transaction, so that transaction is
    /// in it whole (the read returned fewer rows than it asked for). False when the read hit its limit and
    /// the last transaction may continue in the next batch.
    /// </summary>
    public bool EndsWithWholeTransaction { get; init; }

    /// <summary>
    /// By record seq: the document before and after that change, for records written to a path a hook
    /// registered <c>withSnapshots</c> covers (and only when the consumer asked: <see cref="IChangeFeedConsumer.WantsSnapshots"/>).
    /// </summary>
    public IReadOnlyDictionary<long, (Document? Before, Document? After)> Snapshots { get; init; } =
        new Dictionary<long, (Document? Before, Document? After)>();
}

/// <summary>
/// Resume tokens for live queries and document listeners. A token says "this state reflects every change
/// written by transactions up to T" and is encoded as <c>-(T + 1)</c>: negative, so it can't be mistaken
/// for the feed sequence numbers older versions issued (those are ≥ 0 and are treated as unknown, which
/// costs one full snapshot). Clients store and echo the number without reading it.
/// </summary>
public static class ResumeTokens
{
    public static long Encode(ulong throughTxId) => -(long)throughTxId - 1;

    /// <summary>The transaction a token covers through; false for a token from before 10.5.0.</summary>
    public static bool TryDecode(long token, out ulong throughTxId)
    {
        throughTxId = token < 0 ? (ulong)(-(token + 1)) : 0;
        return token < 0;
    }

    /// <summary>The token after applying <paramref name="batch"/>: through its last transaction if that is whole, else the one before.</summary>
    public static long After(ChangeBatch batch)
    {
        var last = batch.Records[^1].TxId;
        return Encode(batch.EndsWithWholeTransaction || last == 0 ? last : last - 1);
    }
}

public interface IChangeFeedConsumer
{
    /// <summary>
    /// Non-null → durable consumer: a cursor row is persisted per this name; the pump
    /// creates a <see cref="DurableConsumerRunner"/> for it. Null → ephemeral (current behaviour).
    /// </summary>
    string? DurableName => null;

    /// <summary>True when the consumer reads <see cref="ChangeBatch.Snapshots"/> (they're loaded only then).</summary>
    bool WantsSnapshots => false;

    Task OnBatchAsync(ChangeBatch batch, CancellationToken ct);
}
