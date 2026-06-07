using System.Threading.Channels;
using Npgsql;
using Winche.Database.Documents;
using Winche.Database.Querying;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Matching;
using Winche.Database.Runtime.ChangeFeed;

namespace Winche.Database.Runtime.Listening;

/// <summary>
/// Live-query groups keyed by QueryKey (identical queries share state + requery work).
/// As an IChangeFeedConsumer it: checks relevance per group (cached predicate / membership),
/// requeries ONCE per batch, diffs ordered state, and broadcasts coalescing snapshots (spec §4).
/// </summary>
public sealed class ListenerRegistry(NpgsqlDataSource source) : IChangeFeedConsumer
{
    private readonly ChangeFeedReader _reader = new(source);
    private readonly Dictionary<string, ListenerGroup> _groups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _byCollection = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public int GroupCount { get { lock (_gate) return _groups.Count; } }

    // ── Subscribe / unsubscribe ───────────────────────────────────────────────

    public IQueryListener Listen(QueryAst query, ListenOptions? listenOptions = null)
    {
        var key = QueryKey.Compute(query);
        ListenerGroup group;
        lock (_gate)
        {
            // I4: if the existing group was disposed (last handle left while we were waiting for _gate),
            // replace it with a fresh group so this handle always joins a live group.
            if (!_groups.TryGetValue(key, out group!) || group.Disposed)
            {
                group = new ListenerGroup(key, query);
                _groups[key] = group;
                if (!_byCollection.TryGetValue(query.Collection, out var set))
                    _byCollection[query.Collection] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(key);
            }
        }

        var handle = new ListenerHandle(this, group);
        // I3: do NOT add handle to group.Handles here; DeliverInitialAsync does it under the
        // semaphore, immediately before pushing the initial snapshot, so the handle never
        // receives a diff before its initial snapshot.

        _ = DeliverInitialAsync(group, handle, listenOptions);     // async init; errors complete the channel
        return handle;
    }

    internal void Remove(ListenerGroup group, ListenerHandle handle)
    {
        lock (group.Gate) group.Handles.Remove(handle);
        lock (_gate)
        {
            lock (group.Gate)
            {
                if (group.Handles.Count > 0) return;
                _groups.Remove(group.Key);
                // I4: mark disposed so any concurrently-initialising handle knows this group is gone.
                group.Disposed = true;
                if (_byCollection.TryGetValue(group.Query.Collection, out var set))
                {
                    set.Remove(group.Key);
                    if (set.Count == 0) _byCollection.Remove(group.Query.Collection);
                }
            }
        }
    }

    private async Task DeliverInitialAsync(ListenerGroup group, ListenerHandle handle, ListenOptions? listenOptions)
    {
        try
        {
            await group.Semaphore.WaitAsync();
            try
            {
                // I4: if the group was disposed while we waited for the semaphore, re-register it
                // so the handle always joins a group that is present in _groups/_byCollection.
                if (group.Disposed)
                {
                    lock (_gate)
                    {
                        // Only re-register if the dictionary still shows the disposed group (or nothing).
                        if (!_groups.TryGetValue(group.Key, out var current) || current == group)
                        {
                            group.Disposed = false;
                            _groups[group.Key] = group;
                            if (!_byCollection.TryGetValue(group.Query.Collection, out var set))
                                _byCollection[group.Query.Collection] = set = new HashSet<string>(StringComparer.Ordinal);
                            set.Add(group.Key);
                        }
                        // else: a fresher group already replaced us — just proceed to join silently.
                    }
                }

                if (!group.Initialized)
                {
                    // I2: read LastSeq BEFORE the query so any concurrent write between the two is
                    // captured in the feed cursor; errs toward spurious-but-safe initial emission on resume.
                    group.LastSeq = await _reader.GetMaxSeqAsync();
                    group.State = (await RunQueryAsync(group.Query)).Documents;
                    group.Initialized = true;
                }

                // I1: provable resume suppression (spec §6): suppress the initial snapshot only when
                // we can prove nothing relevant happened since the resume token.
                if (listenOptions?.ResumeFrom is { } seq)
                {
                    var minSeq = await _reader.GetMinSeqAsync();
                    // minSeq == 0 means the feed is empty (never written or fully pruned); we cannot
                    // distinguish "no rows ever" from "all rows pruned" — treat as not-covered to be safe.
                    // tokenStillCovered: feed is non-empty AND the token falls within the retained window.
                    var tokenStillCovered = minSeq > 0 && seq >= minSeq - 1;
                    if (tokenStillCovered &&
                        !await _reader.HasChangesAfterAsync(seq, group.Query.Collection))
                    {
                        // I3: join the group silently so the handle still receives future diffs.
                        lock (group.Gate) group.Handles.Add(handle);
                        return;                                                 // provably nothing relevant
                    }
                }

                var initial = new QuerySnapshot(
                    group.State,
                    SnapshotDiff.Compute([], group.State),
                    DateTimeOffset.UtcNow,
                    group.LastSeq);

                // I3: add handle to group under the semaphore, immediately before pushing the initial
                // snapshot, so it can never receive a diff before seeing the initial snapshot.
                lock (group.Gate) group.Handles.Add(handle);
                handle.Push(initial);
            }
            finally { group.Semaphore.Release(); }
        }
        catch (Exception ex)
        {
            handle.Fail(ex);
        }
    }

    // ── Feed consumption ──────────────────────────────────────────────────────

    public async Task OnBatchAsync(ChangeBatch batch, CancellationToken ct)
    {
        List<ListenerGroup> candidates;
        lock (_gate)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in batch.Records)
                if (_byCollection.TryGetValue(record.Collection, out var set))
                    keys.UnionWith(set);
            candidates = [.. keys.Select(k => _groups.GetValueOrDefault(k)).OfType<ListenerGroup>()];
        }

        foreach (var group in candidates)
        {
            await group.Semaphore.WaitAsync(ct);
            try
            {
                // I5: a group marked Dirty (from a previous processing error) is always requeried,
                // so it heals on the next relevant batch rather than waiting for a matching change.
                if (!group.Initialized) continue;
                if (!group.Dirty && !IsRelevant(group, batch)) continue;
                group.Dirty = false;  // clear before requery; set again on failure

                IReadOnlyList<Document> newState;
                IReadOnlyList<DocumentChangeInfo> changes;
                try
                {
                    newState = (await RunQueryAsync(group.Query, ct)).Documents;
                    changes = SnapshotDiff.Compute(group.State, newState);
                }
                catch (Exception)
                {
                    // I5: mark dirty so the group retries on the next batch
                    group.Dirty = true;
                    continue;
                }

                group.State = newState;
                group.LastSeq = batch.Records[^1].Seq;
                if (changes.Count == 0) continue;

                var snapshot = new QuerySnapshot(newState, changes,
                    batch.Records[^1].CommitTime, group.LastSeq);
                List<ListenerHandle> handles;
                lock (group.Gate) handles = [.. group.Handles];
                foreach (var handle in handles)
                    handle.Push(snapshot);
            }
            finally { group.Semaphore.Release(); }
        }
    }

    private static bool IsRelevant(ListenerGroup group, ChangeBatch batch)
    {
        foreach (var record in batch.Records)
        {
            if (record.Collection != group.Query.Collection) continue;

            if (record.Type == ChangeType.Removed)
            {
                if (group.StatePaths.Contains(record.Path)) return true;
                continue;
            }

            if (group.StatePaths.Contains(record.Path)) return true;          // member changed
            if (!batch.Documents.TryGetValue(record.Path, out var doc)) return true;   // gone again → conservative
            if (group.PredicateFailed) return true;
            if (group.Predicate is null) return true;                          // unfiltered query
            try
            {
                if (FilterEvaluator.Matches(group.Predicate, doc.Path, doc.Fields)) return true;
            }
            catch
            {
                return true;                                                   // evaluation failure → conservative
            }
        }
        return false;
    }

    private async Task<QueryResult> RunQueryAsync(QueryAst query, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        return await new QueryExecutor(conn, null).ExecuteAsync(query, ct);
    }

    // ── Group / handle ────────────────────────────────────────────────────────

    internal sealed class ListenerGroup
    {
        public ListenerGroup(string key, QueryAst query)
        {
            Key = key;
            Query = query;
            PredicateFailed = !ChangeMatcher.TryPreparePredicate(query, out var predicate);
            Predicate = predicate;
        }

        public string Key { get; }
        public QueryAst Query { get; }
        public FilterAst? Predicate { get; }
        public bool PredicateFailed { get; }
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public object Gate { get; } = new();
        public List<ListenerHandle> Handles { get; } = [];
        public bool Initialized { get; set; }
        public long LastSeq { get; set; }

        /// <summary>I4: Set by Remove when the group is deleted; causes Listen to replace the group.</summary>
        public bool Disposed { get; set; }

        /// <summary>I5: Set when a requery fails; causes the next relevant batch to unconditionally requery.</summary>
        public bool Dirty { get; set; }

        private IReadOnlyList<Document> _state = [];
        private HashSet<string> _statePaths = new(StringComparer.Ordinal);

        public IReadOnlyList<Document> State
        {
            get => _state;
            set
            {
                _state = value;
                _statePaths = new HashSet<string>(value.Select(d => d.Path), StringComparer.Ordinal);
            }
        }

        public IReadOnlySet<string> StatePaths => _statePaths;
    }

    internal sealed class ListenerHandle : IQueryListener
    {
        private readonly ListenerRegistry _registry;
        private readonly ListenerGroup _group;
        private readonly Channel<QuerySnapshot> _channel = Channel.CreateBounded<QuerySnapshot>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });

        internal ListenerHandle(ListenerRegistry registry, ListenerGroup group)
        {
            _registry = registry;
            _group = group;
        }

        internal void Push(QuerySnapshot snapshot) => _channel.Writer.TryWrite(snapshot);
        internal void Fail(Exception ex) => _channel.Writer.TryComplete(ex);

        public IAsyncEnumerable<QuerySnapshot> Snapshots(CancellationToken ct = default) =>
            _channel.Reader.ReadAllAsync(ct);

        public ValueTask DisposeAsync()
        {
            _registry.Remove(_group, this);
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
