using Winche.Database.Documents;

namespace Winche.Database.Runtime.Listening;

public enum ListenChangeType { Added, Modified, Removed }

/// <summary>One docChange: indices are positions in the previous (Old) / new (New) ordered snapshot; -1 = absent.</summary>
public sealed record DocumentChangeInfo(ListenChangeType Type, Document Document, int OldIndex, int NewIndex);

/// <summary>
/// A consistent ordered result + the changes that produced it (query listener contract).
/// <para>
/// <see cref="Documents"/> is always the self-contained authoritative state of the query result.
/// </para>
/// <para>
/// <see cref="Changes"/> indices are relative to the listener's previous <em>delivered</em>
/// snapshot — which, under the bounded coalescing channel (capacity 1, DropOldest), may differ
/// from the previous snapshot emitted by the shared group. Always use <see cref="Documents"/>
/// as the ground truth; treat <see cref="Changes"/> as a hint for efficient UI updates.
/// </para>
/// </summary>
public sealed record QuerySnapshot(
    IReadOnlyList<Document> Documents,
    IReadOnlyList<DocumentChangeInfo> Changes,
    DateTimeOffset ReadTime,
    long ResumeToken,
    bool Current = false);

public sealed record ListenOptions(long? ResumeFrom = null);

/// <summary>
/// A live query subscription. Snapshot semantics are documented on
/// <see cref="ISubscriptionListener{TSnapshot}"/>; concurrent enumerators split the stream
/// (each snapshot is delivered to exactly one enumerator) — create a single enumerator per listener.
/// </summary>
public interface IQueryListener : ISubscriptionListener<QuerySnapshot>
{
    /// <summary>Unguarded batch existence check at the data layer — returns the subset of
    /// [paths] that currently exist. Used by the WS pump (one round-trip per delta) to classify
    /// removed results as true deletes (absent) vs window/filter exits (still present).</summary>
    Task<IReadOnlySet<string>> ExistingAsync(IReadOnlyList<string> paths, CancellationToken ct);
}
