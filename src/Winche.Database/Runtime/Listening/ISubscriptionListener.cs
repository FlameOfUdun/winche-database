namespace Winche.Database.Runtime.Listening;

/// <summary>
/// A live subscription that streams point-in-time snapshots of type <typeparamref name="TSnapshot"/>.
/// The underlying channel is bounded + coalescing (capacity 1, drop-oldest): an unconsumed snapshot
/// is replaced by the next, so a slow reader always observes the newest state, never a backlog.
/// Create a single enumerator per listener.
/// </summary>
public interface ISubscriptionListener<out TSnapshot> : IAsyncDisposable
{
    IAsyncEnumerable<TSnapshot> Snapshots(CancellationToken ct = default);
}
