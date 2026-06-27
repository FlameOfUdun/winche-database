using Npgsql;
using Winche.Database.Runtime.ChangeFeed;

namespace Winche.Database.Runtime.Listening;

/// <summary>
/// Generic live-subscription engine: groups keyed by <see cref="SubscriptionGroup{TSnapshot}.Key"/>
/// (identical subscriptions share state + work), dispatched per change batch by relevance bucket,
/// recomputed once per relevant batch, diffed, and broadcast over coalescing channels.
/// Subclasses supply the query- or document-specific behaviour through the abstract hooks.
/// </summary>
public abstract class LiveSubscriptionRegistry<TGroup, TSnapshot>(NpgsqlDataSource source) : IChangeFeedConsumer
    where TGroup : SubscriptionGroup<TSnapshot>
{
    private readonly Dictionary<string, TGroup> _groups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _byIndexKey = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>The data source, exposed so subclasses can open their own connections without recapturing the ctor parameter.</summary>
    protected NpgsqlDataSource Source { get; } = source;

    protected ChangeFeedReader Reader { get; } = new(source);

    internal int GroupCount { get { lock (_gate) return _groups.Count; } }

    // ── Hooks ──────────────────────────────────────────────────────────────────

    /// <summary>Create a handle bound to <paramref name="group"/>; <paramref name="onDispose"/> must reach Remove.</summary>
    protected abstract SubscriptionHandleBase<TSnapshot> CreateHandle(
        TGroup group, Func<SubscriptionHandleBase<TSnapshot>, ValueTask> onDispose);

    /// <summary>Load the group's initial state (point get, or query) and store it on the group.</summary>
    protected abstract Task InitializeStateAsync(TGroup group, CancellationToken ct);

    /// <summary>True when <paramref name="batch"/> can change this group's result.</summary>
    protected abstract bool IsRelevant(TGroup group, ChangeBatch batch);

    /// <summary>
    /// Recompute the group's new state from <paramref name="batch"/>; store the new state on the group
    /// and return the snapshot to emit plus whether anything changed.
    /// </summary>
    protected abstract Task<(TSnapshot? Snapshot, bool Changed)> RecomputeAsync(
        TGroup group, ChangeBatch batch, CancellationToken ct);

    /// <summary>Build the initial snapshot from the group's already-initialized state.</summary>
    protected abstract TSnapshot BuildInitialSnapshot(TGroup group);

    /// <summary>True when the feed has a change relevant to this group after <paramref name="resumeSeq"/>.</summary>
    protected abstract Task<bool> HasRelevantChangesAfterAsync(TGroup group, long resumeSeq, CancellationToken ct);

    /// <summary>The relevance bucket a change record belongs to (collection, or document path).</summary>
    protected abstract string IndexKeyOf(ChangeRecord record);

    // ── Subscribe / unsubscribe ────────────────────────────────────────────────

    protected SubscriptionHandleBase<TSnapshot> Subscribe(
        string key, string indexKey, Func<string, string, TGroup> createGroup, ListenOptions? listenOptions)
    {
        TGroup group;
        lock (_gate)
        {
            // I4: if the existing group was disposed (last handle left while we were waiting for _gate),
            // replace it with a fresh group so this handle always joins a live group.
            if (!_groups.TryGetValue(key, out group!) || group.Disposed)
            {
                group = createGroup(key, indexKey);
                _groups[key] = group;
                if (!_byIndexKey.TryGetValue(indexKey, out var set))
                    _byIndexKey[indexKey] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(key);
            }
        }

        var handle = CreateHandle(group, h => RemoveAsync(group, h));
        // I3: the handle is NOT added to group.Handles here; DeliverInitialAsync adds it under the
        // semaphore, immediately before the initial snapshot, so it never sees a diff first.
        _ = DeliverInitialAsync(group, handle, listenOptions);
        return handle;
    }

    private ValueTask RemoveAsync(TGroup group, SubscriptionHandleBase<TSnapshot> handle)
    {
        lock (group.Gate) group.Handles.Remove(handle);
        lock (_gate)
        {
            lock (group.Gate)
            {
                if (group.Handles.Count > 0) return ValueTask.CompletedTask;
                _groups.Remove(group.Key);
                // I4: mark disposed so any concurrently-initialising handle knows this group is gone.
                group.Disposed = true;
                if (_byIndexKey.TryGetValue(group.IndexKey, out var set))
                {
                    set.Remove(group.Key);
                    if (set.Count == 0) _byIndexKey.Remove(group.IndexKey);
                }
            }
        }
        return ValueTask.CompletedTask;
    }

    private async Task DeliverInitialAsync(TGroup group, SubscriptionHandleBase<TSnapshot> handle, ListenOptions? listenOptions)
    {
        try
        {
            await group.Semaphore.WaitAsync();
            try
            {
                // I4: if the group was disposed while we waited for the semaphore, re-register it
                // so the handle always joins a group that is present in _groups/_byIndexKey.
                if (group.Disposed)
                {
                    lock (_gate)
                    {
                        // Only re-register if the dictionary still shows the disposed group (or nothing).
                        if (!_groups.TryGetValue(group.Key, out var current) || current == group)
                        {
                            group.Disposed = false;
                            _groups[group.Key] = group;
                            if (!_byIndexKey.TryGetValue(group.IndexKey, out var set))
                                _byIndexKey[group.IndexKey] = set = new HashSet<string>(StringComparer.Ordinal);
                            set.Add(group.Key);
                        }
                        // else: a fresher group already replaced us — just proceed to join silently.
                    }
                }

                if (!group.Initialized)
                {
                    // I2: read LastSeq BEFORE the load so any concurrent write between the two is
                    // captured in the feed cursor; errs toward spurious-but-safe initial emission on resume.
                    group.LastSeq = await Reader.GetMaxSeqAsync();
                    await InitializeStateAsync(group, CancellationToken.None);
                    group.Initialized = true;
                }

                // I1: provable resume suppression: suppress the initial snapshot only when
                // we can prove nothing relevant happened since the resume token.
                if (listenOptions?.ResumeFrom is { } seq && await IsResumeCoveredAsync(group, seq, CancellationToken.None))
                {
                    // I3: join the group silently so the handle still receives future diffs.
                    lock (group.Gate) group.Handles.Add(handle);
                    return;                                                 // provably nothing relevant
                }

                // I3: add handle to group under the semaphore, immediately before pushing the initial
                // snapshot, so it can never receive a diff before seeing the initial snapshot.
                lock (group.Gate) group.Handles.Add(handle);
                handle.Push(BuildInitialSnapshot(group));
            }
            finally { group.Semaphore.Release(); }
        }
        catch (Exception ex)
        {
            handle.Fail(ex);
        }
    }

    private async Task<bool> IsResumeCoveredAsync(TGroup group, long resumeSeq, CancellationToken ct)
    {
        var minSeq = await Reader.GetMinSeqAsync(ct);
        // minSeq == 0 means the feed is empty (never written or fully pruned); we cannot distinguish
        // "no rows ever" from "all rows pruned" — treat as not-covered to be safe.
        // tokenStillCovered: feed is non-empty AND the token falls within the retained window.
        var tokenStillCovered = minSeq > 0 && resumeSeq >= minSeq - 1;
        return tokenStillCovered && !await HasRelevantChangesAfterAsync(group, resumeSeq, ct);
    }

    // ── Feed consumption ───────────────────────────────────────────────────────

    public async Task OnBatchAsync(ChangeBatch batch, CancellationToken ct)
    {
        List<TGroup> candidates;
        lock (_gate)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in batch.Records)
            {
                if (_byIndexKey.TryGetValue(IndexKeyOf(record), out var set))
                    keys.UnionWith(set);
            }
            candidates = [.. keys.Select(k => _groups.GetValueOrDefault(k)).OfType<TGroup>()];
        }

        foreach (var group in candidates)
        {
            await group.Semaphore.WaitAsync(ct);
            try
            {
                // I5: a group marked Dirty (from a previous processing error) is always recomputed,
                // so it heals on the next relevant batch rather than waiting for a matching change.
                if (!group.Initialized) continue;
                if (!group.Dirty && !IsRelevant(group, batch)) continue;
                group.Dirty = false;   // clear before recompute; set again on failure

                (TSnapshot? Snapshot, bool Changed) result;
                try { result = await RecomputeAsync(group, batch, ct); }
                catch (Exception)
                {
                    // I5: mark dirty so the group retries on the next batch.
                    group.Dirty = true;
                    continue;
                }

                group.LastSeq = batch.Records[^1].Seq;
                if (!result.Changed) continue;

                List<SubscriptionHandleBase<TSnapshot>> handles;
                lock (group.Gate) handles = [.. group.Handles];
                foreach (var handle in handles) handle.Push(result.Snapshot!);
            }
            finally { group.Semaphore.Release(); }
        }
    }
}
