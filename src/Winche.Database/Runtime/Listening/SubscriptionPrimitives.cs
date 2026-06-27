using System.Threading.Channels;

namespace Winche.Database.Runtime.Listening;

/// <summary>
/// Per-subscription group state shared by all live registries. One group is shared by every handle
/// with the same <see cref="Key"/> (identical subscriptions share initial load + update work).
/// Concurrency invariants (preserved from the original query registry):
///   I2 read LastSeq before the initial load; I3 add a handle under the semaphore immediately before
///   its initial snapshot; I4 a disposed group is re-registered/replaced on a racing subscribe;
///   I5 a group whose update failed is marked <see cref="Dirty"/> and unconditionally recomputed next batch.
/// </summary>
public abstract class SubscriptionGroup<TSnapshot>
{
    /// <summary>Identity that de-duplicates subscriptions (query key, or document path).</summary>
    public required string Key { get; init; }

    /// <summary>Relevance bucket used to dispatch change batches (collection, or document path).</summary>
    public required string IndexKey { get; init; }

    public SemaphoreSlim Semaphore { get; } = new(1, 1);
    public object Gate { get; } = new();
    public List<SubscriptionHandleBase<TSnapshot>> Handles { get; } = [];
    public bool Initialized { get; set; }

    /// <summary>I4: set by Remove when the group is deleted; causes a racing subscribe to replace it.</summary>
    public bool Disposed { get; set; }

    /// <summary>I5: set when an update fails; causes the next relevant batch to unconditionally recompute.</summary>
    public bool Dirty { get; set; }

    public long LastSeq { get; set; }
}

/// <summary>
/// Shared handle plumbing: a capacity-1 drop-oldest channel plus push/fail/stream/dispose.
/// Concrete registries derive a tiny handle that also implements the public listener interface
/// (<see cref="IQueryListener"/> / <see cref="IDocumentListener"/>).
/// </summary>
public abstract class SubscriptionHandleBase<TSnapshot> : ISubscriptionListener<TSnapshot>
{
    private readonly Channel<TSnapshot> _channel = Channel.CreateBounded<TSnapshot>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Func<SubscriptionHandleBase<TSnapshot>, ValueTask> _onDispose;

    protected SubscriptionHandleBase(Func<SubscriptionHandleBase<TSnapshot>, ValueTask> onDispose) =>
        _onDispose = onDispose;

    internal void Push(TSnapshot snapshot) => _channel.Writer.TryWrite(snapshot);
    internal void Fail(Exception ex) => _channel.Writer.TryComplete(ex);

    public IAsyncEnumerable<TSnapshot> Snapshots(CancellationToken ct = default) =>
        _channel.Reader.ReadAllAsync(ct);

    public async ValueTask DisposeAsync()
    {
        await _onDispose(this);
        _channel.Writer.TryComplete();
    }
}
