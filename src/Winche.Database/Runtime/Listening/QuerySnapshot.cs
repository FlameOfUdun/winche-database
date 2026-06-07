using Winche.Database.Documents;

namespace Winche.Database.Runtime.Listening;

public enum ListenChangeType { Added, Modified, Removed }

/// <summary>One docChange: indices are positions in the previous (Old) / new (New) ordered snapshot; -1 = absent.</summary>
public sealed record DocumentChangeInfo(ListenChangeType Type, Document Document, int OldIndex, int NewIndex);

/// <summary>
/// A consistent ordered result + the changes that produced it (Firestore listener contract).
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
    long ResumeToken);

public sealed record ListenOptions(long? ResumeFrom = null);

/// <summary>
/// A live query subscription. Implemented in Runtime Plan 3.
/// </summary>
public interface IQueryListener : IAsyncDisposable
{
    /// <summary>
    /// Streams query snapshots as they arrive.
    /// <para>
    /// The underlying channel is a bounded coalescing channel (capacity 1, DropOldest): when a
    /// snapshot is not consumed before the next one arrives, the older one is silently dropped and
    /// only the newest state is delivered. Concurrent enumerators split the stream (each snapshot
    /// is delivered to exactly one enumerator) — create a single enumerator per listener.
    /// </para>
    /// </summary>
    IAsyncEnumerable<QuerySnapshot> Snapshots(CancellationToken ct = default);
}
