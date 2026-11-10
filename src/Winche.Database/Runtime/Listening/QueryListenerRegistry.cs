using Npgsql;
using Winche.Database.DependencyInjection;
using Winche.Database.Documents;
using Winche.Database.Querying;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Matching;
using Winche.Database.Querying.Planning;
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
            QueryKey.Compute(query), query.Collection,
            (key, indexKey) =>
            {
                var ok = ChangeMatcher.TryPreparePredicate(query, out var predicate);
                return new QueryGroup
                {
                    Key = key, IndexKey = indexKey, Query = query, Predicate = predicate, PredicateFailed = !ok,
                    SortKeys = ok ? InMemoryQuery.SortKeysFor(query) : null,
                };
            },
            listenOptions);

    // ── Hooks ──────────────────────────────────────────────────────────────────

    protected override SubscriptionHandleBase<QuerySnapshot> CreateHandle(
        QueryGroup group, Func<SubscriptionHandleBase<QuerySnapshot>, ValueTask> onDispose) =>
        new QueryHandle(onDispose, ExistingAsync);

    protected override async Task InitializeStateAsync(QueryGroup group, CancellationToken ct)
    {
        group.State = (await RunQueryAsync(group.Query, ct)).Documents;
        group.LastQueried = DateTimeOffset.UtcNow;
    }

    protected override bool IsRelevant(QueryGroup group, ChangeBatch batch)
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

    protected override async Task<(QuerySnapshot? Snapshot, bool Changed)> RecomputeAsync(
        QueryGroup group, ChangeBatch batch, CancellationToken ct)
    {
        IReadOnlyList<Document>? newState = null;
        if (_config.ApplyChangesInMemory
            && group.SortKeys is { } keys
            && DateTimeOffset.UtcNow - group.LastQueried < _config.ResyncInterval)
            newState = InMemoryQuery.TryApply(group.Query, group.Predicate, keys, group.State, batch);
        if (newState is null)
        {
            newState = (await RunQueryAsync(group.Query, ct)).Documents;
            group.LastQueried = DateTimeOffset.UtcNow;
        }
        var changes = SnapshotDiff.Compute(group.State, newState);
        group.State = newState;
        if (changes.Count == 0) return (null, false);
        return (new QuerySnapshot(newState, changes, batch.Records[^1].CommitTime, batch.Records.Max(r => r.Seq)), true);
    }

    protected override QuerySnapshot BuildInitialSnapshot(QueryGroup group) =>
        new(group.State, SnapshotDiff.Compute([], group.State), DateTimeOffset.UtcNow, group.LastSeq);

    protected override QuerySnapshot BuildCurrentMarker(QueryGroup group) =>
        new([], [], DateTimeOffset.UtcNow, group.LastSeq, Current: true);

    protected override Task<bool> HasRelevantChangesAfterAsync(QueryGroup group, long resumeSeq, CancellationToken ct) =>
        Reader.HasQueryChangesAfterAsync(resumeSeq, group.Query.Collection, ct);

    protected override string IndexKeyOf(ChangeRecord record) => record.Collection;

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

        /// <summary>The sort order, when this query's result can be kept up to date in memory; else null.</summary>
        internal IReadOnlyList<SortKey>? SortKeys { get; init; }

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
