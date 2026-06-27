using Npgsql;
using Winche.Database.Documents;
using Winche.Database.Querying;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Matching;
using Winche.Database.Runtime.ChangeFeed;

namespace Winche.Database.Runtime.Listening;

/// <summary>
/// Live-query registry: groups keyed by <see cref="QueryKey"/>; relevance bucket is the collection.
/// Each relevant batch triggers ONE requery; the ordered result is diffed and broadcast.
/// </summary>
public sealed class QueryListenerRegistry(NpgsqlDataSource source, CollectionIndexResolver? scopes = null)
    : LiveSubscriptionRegistry<QueryListenerRegistry.QueryGroup, QuerySnapshot>(source)
{
    public IQueryListener Listen(Query query, ListenOptions? listenOptions = null) =>
        (IQueryListener)Subscribe(
            QueryKey.Compute(query), query.Collection,
            (key, indexKey) =>
            {
                var ok = ChangeMatcher.TryPreparePredicate(query, out var predicate);
                return new QueryGroup { Key = key, IndexKey = indexKey, Query = query, Predicate = predicate, PredicateFailed = !ok };
            },
            listenOptions);

    // ── Hooks ──────────────────────────────────────────────────────────────────

    protected override SubscriptionHandleBase<QuerySnapshot> CreateHandle(
        QueryGroup group, Func<SubscriptionHandleBase<QuerySnapshot>, ValueTask> onDispose) =>
        new QueryHandle(onDispose);

    protected override async Task InitializeStateAsync(QueryGroup group, CancellationToken ct) =>
        group.State = (await RunQueryAsync(group.Query, ct)).Documents;

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
        var newState = (await RunQueryAsync(group.Query, ct)).Documents;
        var changes = SnapshotDiff.Compute(group.State, newState);
        group.State = newState;
        if (changes.Count == 0) return (null, false);
        return (new QuerySnapshot(newState, changes, batch.Records[^1].CommitTime, batch.Records[^1].Seq), true);
    }

    protected override QuerySnapshot BuildInitialSnapshot(QueryGroup group) =>
        new(group.State, SnapshotDiff.Compute([], group.State), DateTimeOffset.UtcNow, group.LastSeq);

    protected override Task<bool> HasRelevantChangesAfterAsync(QueryGroup group, long resumeSeq, CancellationToken ct) =>
        Reader.HasQueryChangesAfterAsync(resumeSeq, group.Query.Collection, ct);

    protected override string IndexKeyOf(ChangeRecord record) => record.Collection;

    private async Task<QueryResult> RunQueryAsync(Query query, CancellationToken ct = default)
    {
        await using var conn = await Source.OpenConnectionAsync(ct);
        return await new QueryExecutor(conn, null, scopes).ExecuteAsync(query, ct);
    }

    // ── Group / handle ─────────────────────────────────────────────────────────

    public sealed class QueryGroup : SubscriptionGroup<QuerySnapshot>
    {
        public required Query Query { get; init; }
        public Filter? Predicate { get; init; }
        public bool PredicateFailed { get; init; }

        private IReadOnlyList<Document> _state = [];
        private HashSet<string> _statePaths = new(StringComparer.Ordinal);

        public IReadOnlyList<Document> State
        {
            get => _state;
            set { _state = value; _statePaths = new HashSet<string>(value.Select(d => d.Path), StringComparer.Ordinal); }
        }

        public IReadOnlySet<string> StatePaths => _statePaths;
    }

    private sealed class QueryHandle(Func<SubscriptionHandleBase<QuerySnapshot>, ValueTask> onDispose)
        : SubscriptionHandleBase<QuerySnapshot>(onDispose), IQueryListener;
}
