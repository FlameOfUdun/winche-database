using Npgsql;
using Winche.Database.DependencyInjection;
using Winche.Database.Documents;
using Winche.Database.Querying;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Matching;
using Winche.Database.Runtime.ChangeFeed;

namespace Winche.Database.Runtime.Listening;

/// <summary>
/// Live-query registry: groups keyed by <see cref="QueryKey"/>; relevance bucket is the collection.
/// A relevant batch is applied to the group's result in memory when that gives the exact result (the
/// changed documents are in the batch; the filter and sort order are evaluated here, as the SQL does);
/// otherwise — and every <see cref="ListenerConfig.ResyncInterval"/> — the query is re-run. The ordered
/// result is diffed and broadcast.
/// </summary>
public sealed class QueryListenerRegistry(NpgsqlDataSource source, CollectionIndexResolver? scopes = null, ListenerConfig? config = null)
    : LiveSubscriptionRegistry<QueryListenerRegistry.QueryGroup, QuerySnapshot>(source, config?.MaxParallelUpdates ?? 8)
{
    private readonly ListenerConfig _config = config ?? new ListenerConfig();

    public IQueryListener Listen(Query query, ListenOptions? listenOptions = null) =>
        (IQueryListener)Subscribe(
            QueryKey.Compute(query), BucketOf(query),
            (key, indexKey) =>
            {
                var ok = ChangeMatcher.TryPreparePredicate(query, out var predicate);
                return new QueryGroup
                {
                    Key = key, IndexKey = indexKey, Query = query, Predicate = predicate, PredicateFailed = !ok,
                    InMemory = ok && _config.ApplyChangesInMemory ? InMemoryQuery.For(query, _config.HeldBeyondLimit) : null,
                };
            },
            listenOptions);

    // ── Hooks ──────────────────────────────────────────────────────────────────

    protected override SubscriptionHandleBase<QuerySnapshot> CreateHandle(
        QueryGroup group, Func<SubscriptionHandleBase<QuerySnapshot>, ValueTask> onDispose) =>
        new QueryHandle(onDispose, ExistingAsync);

    protected override async Task InitializeStateAsync(QueryGroup group, CancellationToken ct) =>
        await RequeryAsync(group, ct);

    /// <summary>Runs the query; for an in-memory query, fetches the held window (whole documents, limit + spares).</summary>
    private async Task RequeryAsync(QueryGroup group, CancellationToken ct)
    {
        if (group.InMemory is { } live)
        {
            group.Held = live.FromFetch((await RunQueryAsync(live.FetchQuery, ct)).Documents);
            group.State = live.Result(group.Held);
        }
        else
        {
            group.State = (await RunQueryAsync(group.Query, ct)).Documents;
        }
        group.LastQueried = DateTimeOffset.UtcNow;
    }

    protected override bool IsRelevant(QueryGroup group, ChangeBatch batch)
    {
        foreach (var record in batch.Records)
        {
            if (!InScope(group.Query, record.Collection)) continue;

            if (record.Type == ChangeType.Removed)
            {
                if (group.TrackedPaths.Contains(record.Path)) return true;
                continue;
            }

            if (group.TrackedPaths.Contains(record.Path)) return true;        // member (or held spare) changed
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

    protected override async Task<(QuerySnapshot? Snapshot, bool Changed)> RecomputeAsync(
        QueryGroup group, ChangeBatch batch, CancellationToken ct)
    {
        var oldState = group.State;
        if (group.InMemory is { } live && group.Held is { } held
            && DateTimeOffset.UtcNow - group.LastQueried < _config.ResyncInterval
            && live.TryApply(held, batch) is { } applied)
        {
            group.Held = applied;
            group.State = live.Result(applied);
        }
        else
        {
            await RequeryAsync(group, ct);
        }
        var newState = group.State;
        var changes = SnapshotDiff.Compute(oldState, newState);
        if (changes.Count == 0) return (null, false);
        return (new QuerySnapshot(newState, changes, batch.Records[^1].CommitTime, ResumeTokens.After(batch)), true);
    }

    protected override QuerySnapshot BuildInitialSnapshot(QueryGroup group) =>
        new(group.State, SnapshotDiff.Compute([], group.State), DateTimeOffset.UtcNow, group.ResumeToken);

    protected override QuerySnapshot BuildCurrentMarker(QueryGroup group) =>
        new([], [], DateTimeOffset.UtcNow, group.ResumeToken, Current: true);

    protected override Task<bool> HasRelevantChangesAfterAsync(QueryGroup group, ulong throughTxId, CancellationToken ct) =>
        group.Query.CollectionGroup
            ? Reader.HasCollectionGroupChangesAfterAsync(throughTxId, group.Query.Collection, ct)
            : Reader.HasCollectionChangesAfterAsync(throughTxId, group.Query.Collection, ct);

    // A change is in its collection's bucket and its collection group's ("group:<id>").
    protected override IEnumerable<string> IndexKeysOf(ChangeRecord record) =>
        [record.Collection, GroupBucket(DocumentPathParser.CollectionIdOf(record.Collection))];

    private static string GroupBucket(string collectionId) => "group:" + collectionId;

    private static string BucketOf(Query query) => query.CollectionGroup ? GroupBucket(query.Collection) : query.Collection;

    /// <summary>Whether a change to <paramref name="collection"/> is in <paramref name="query"/>'s collection (or group).</summary>
    internal static bool InScope(Query query, string collection) =>
        query.CollectionGroup ? DocumentPathParser.CollectionIdOf(collection) == query.Collection : collection == query.Collection;

    private int _queriesRun;

    /// <summary>How many times this registry has run a live query against the database (diagnostics).</summary>
    internal int QueriesRun => Volatile.Read(ref _queriesRun);

    private async Task<QueryResult> RunQueryAsync(Query query, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _queriesRun);
        await using var conn = await Source.OpenConnectionAsync(ct);
        return await new QueryExecutor(conn, null, scopes).ExecuteAsync(query, ct);
    }

    // Returns the subset of [paths] that currently exist, in ONE round-trip — so a delta with
    // many removed docs costs a single connection, not one per doc. Checks CURRENT existence
    // (at probe time, slightly after the snapshot's ReadTime); any divergence is corrected by
    // the next authoritative snapshot. Unguarded by design: a list-authorized listener must be
    // able to test existence even without a per-document get rule (existence is not field data).
    private async Task<IReadOnlySet<string>> ExistingAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        if (paths.Count == 0) return new HashSet<string>(StringComparer.Ordinal);
        await using var conn = await Source.OpenConnectionAsync(ct);
        var found = await new DocumentOperations(conn, null).GetManyAsync(paths, ct);
        return found.Keys.ToHashSet(StringComparer.Ordinal);
    }

    // ── Group / handle ─────────────────────────────────────────────────────────

    public sealed class QueryGroup : SubscriptionGroup<QuerySnapshot>
    {
        public required Query Query { get; init; }
        public Filter? Predicate { get; init; }
        public bool PredicateFailed { get; init; }

        /// <summary>How this query is kept up to date in memory; null when it is always re-run.</summary>
        internal InMemoryQuery? InMemory { get; init; }

        private HeldWindow? _held;
        private HashSet<string>? _heldPaths;

        /// <summary>The documents held for in-memory updates (the result plus spares beyond the limit).</summary>
        internal HeldWindow? Held
        {
            get => _held;
            set { _held = value; _heldPaths = value is null ? null : new HashSet<string>(value.Documents.Select(d => d.Path), StringComparer.Ordinal); }
        }

        /// <summary>Paths whose change can alter this query's state: the held documents, else the result.</summary>
        internal IReadOnlySet<string> TrackedPaths => _heldPaths ?? _statePaths;

        /// <summary>When the query was last run against the database.</summary>
        internal DateTimeOffset LastQueried { get; set; }

        private IReadOnlyList<Document> _state = [];
        private HashSet<string> _statePaths = new(StringComparer.Ordinal);

        public IReadOnlyList<Document> State
        {
            get => _state;
            set { _state = value; _statePaths = new HashSet<string>(value.Select(d => d.Path), StringComparer.Ordinal); }
        }

        public IReadOnlySet<string> StatePaths => _statePaths;
    }

    private sealed class QueryHandle(
        Func<SubscriptionHandleBase<QuerySnapshot>, ValueTask> onDispose,
        Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlySet<string>>> existingProbe)
        : SubscriptionHandleBase<QuerySnapshot>(onDispose), IQueryListener
    {
        public Task<IReadOnlySet<string>> ExistingAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
            existingProbe(paths, ct);
    }
}
