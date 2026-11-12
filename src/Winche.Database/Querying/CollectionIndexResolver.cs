using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Winche.Database.DependencyInjection;
using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Planning;

namespace Winche.Database.Querying;

/// <summary>
/// What the declared indexes mean for a query: the collection_id predicate that lets Postgres use a
/// collection-ID partial index, the extra condition an array-membership index needs, and — for a slow
/// query that no declared index serves — a logged suggestion of the index to add.
/// </summary>
public sealed class CollectionIndexResolver
{
    private sealed record IndexState(IReadOnlyList<IndexDefinition> Indexes, HashSet<string> Declared, HashSet<(string CollectionId, string Field)> ArrayIndexed)
    {
        public static IndexState Of(IReadOnlyList<IndexDefinition> indexes) => new(indexes,
            indexes.Select(i => i.CollectionId).ToHashSet(StringComparer.Ordinal),
            indexes.Where(i => i.Kind == IndexKind.ArrayMembership).Select(i => (i.CollectionId, i.Fields[0].Path)).ToHashSet());
    }

    private volatile IndexState _state;
    private IReadOnlyList<IndexDefinition> _indexes => _state.Indexes;
    private HashSet<string> _declaredCollectionIds => _state.Declared;
    private HashSet<(string CollectionId, string Field)> _arrayIndexed => _state.ArrayIndexed;
    private readonly IOptions<WincheDatabaseOptions>? _options;
    private readonly ILogger<CollectionIndexResolver>? _logger;
    private TimeSpan? _slowQuery => _options is null ? TimeSpan.FromMilliseconds(500) : _options.Value.SlowQueryThreshold;
    private readonly ConcurrentDictionary<string, bool> _advised = new(StringComparer.Ordinal);

    public CollectionIndexResolver(
        IEnumerable<IndexDefinition> indexes,
        ILogger<CollectionIndexResolver>? logger = null,
        IOptions<WincheDatabaseOptions>? options = null)
    {
        _state = IndexState.Of([.. indexes]);
        _logger = logger;
        _options = options;
    }

    /// <summary>Follows <paramref name="catalog"/>: index changes apply to the next query.</summary>
    public CollectionIndexResolver(
        Schema.IndexCatalog catalog,
        ILogger<CollectionIndexResolver>? logger = null,
        IOptions<WincheDatabaseOptions>? options = null) : this(catalog.Indexes, logger, options)
    {
        catalog.Changed += () => _state = IndexState.Of(catalog.Indexes);
    }

    /// <summary>Raised when a slow query has no index; the console offers to create the suggested one.</summary>
    public event Action<IndexSuggestion>? Suggested;

    /// <summary>The collection id to add as a predicate (null when no index covers it). A collection group query
    /// always filters by its id, so it needs none.</summary>
    public string? ScopeFor(string collectionPath)
    {
        var id = DocumentPathParser.CollectionIdOf(collectionPath);
        return _declaredCollectionIds.Contains(id) ? id : null;
    }

    // ── Array-membership indexes ───────────────────────────────────────────────

    /// <summary>
    /// The plan with, for each top-level arrayContains / arrayContainsAny / arrayContainsAll on a field that
    /// has an array-membership index, an extra condition that index can serve. The condition is implied by
    /// the filter itself (elements are matched by winche_key either way), so results don't change.
    /// </summary>
    internal LogicalPlan WithArrayIndexConditions(string collectionPath, LogicalPlan plan)
    {
        if (_arrayIndexed.Count == 0) return plan;
        var id = DocumentPathParser.CollectionIdOf(collectionPath);
        var node = plan.Nodes.OfType<FilterNode>().FirstOrDefault();
        if (node is null) return plan;

        var extra = Conjuncts(node.Predicate)
            .OfType<FieldFilter>()
            .Where(f => f.Op is FilterOperator.ArrayContains or FilterOperator.ArrayContainsAny or FilterOperator.ArrayContainsAll
                        && _arrayIndexed.Contains((id, f.Field.ToString())))
            .Select(f => (Filter)new ArrayKeysFilter(f.Field, f.Op,
                f.Op == FilterOperator.ArrayContains ? [f.Operand] : ((Values.ArrayValue)f.Operand).Values))
            .ToList();
        if (extra.Count == 0) return plan;

        var predicate = new CompositeFilter(CompositeOp.And, [node.Predicate, .. extra]);
        return new LogicalPlan([.. plan.Nodes.Select(n => n == node ? new FilterNode(predicate) : n)]);
    }

    private static IEnumerable<Filter> Conjuncts(Filter filter) =>
        filter is CompositeFilter { Op: CompositeOp.And } and
            ? and.Filters.SelectMany(Conjuncts)
            : [filter];

    // ── Index advice ───────────────────────────────────────────────────────────

    /// <summary>
    /// After a query ran: if it was slower than <see cref="WincheDatabaseOptions.SlowQueryThreshold"/> and no
    /// declared index serves it, log (once per collection id and index) the index to add.
    /// </summary>
    internal void Observe(Query query, LogicalPlan plan, TimeSpan elapsed)
    {
        if (_logger is null || _slowQuery is not { } threshold || elapsed < threshold) return;
        var advice = Advise(query, plan);
        if (advice is null || !_advised.TryAdd(advice, true)) return;
        if (SuggestionFor(query, plan) is { } suggestion) Suggested?.Invoke(suggestion with { ElapsedMs = (long)elapsed.TotalMilliseconds });
        _logger.LogWarning(
            "Query on '{Collection}' took {Elapsed} ms and no declared index serves it. Add: {Index}",
            query.Collection, (long)elapsed.TotalMilliseconds, advice);
    }

    /// <summary>The index declaration that would serve <paramref name="query"/>, or null when one exists or none would help.</summary>
    internal string? Advise(Query query, LogicalPlan plan) => SuggestionFor(query, plan)?.Code;

    /// <summary>The index that would serve <paramref name="query"/>, or null when one exists or none would help.</summary>
    internal IndexSuggestion? SuggestionFor(Query query, LogicalPlan plan)
    {
        var id = DocumentPathParser.CollectionIdOf(query.Collection);
        var conjuncts = plan.Nodes.OfType<FilterNode>().FirstOrDefault() is { } f ? Conjuncts(f.Predicate).ToList() : [];

        foreach (var array in conjuncts.OfType<FieldFilter>()
                     .Where(c => c.Op is FilterOperator.ArrayContains or FilterOperator.ArrayContainsAny or FilterOperator.ArrayContainsAll))
        {
            if (!_arrayIndexed.Contains((id, array.Field.ToString())))
                return new IndexSuggestion(id, [new IndexField(array.Field.ToString())], IndexKind.ArrayMembership, false,
                    $"ib.AddArray(\"{id}\", \"{array.Field}\")");
        }

        static bool IsName(FieldPath p) => p.Segments is ["__name__"];
        var equality = conjuncts.OfType<FieldFilter>()
            .Where(c => c.Op is FilterOperator.Eq or FilterOperator.In && !IsName(c.Field))
            .Select(c => c.Field.ToString()).Distinct().ToList();
        var order = (query.OrderBy ?? []).Where(o => !IsName(o.Field))
            .Select(o => (Path: o.Field.ToString(), o.Direction)).Where(o => !equality.Contains(o.Path)).ToList();
        if (order.Count == 0)
        {
            var range = conjuncts.OfType<FieldFilter>()
                .FirstOrDefault(c => c.Op is FilterOperator.Gt or FilterOperator.Gte or FilterOperator.Lt or FilterOperator.Lte && !IsName(c.Field));
            if (range is not null && !equality.Contains(range.Field.ToString()))
                order.Add((range.Field.ToString(), SortDirection.Asc));
        }
        if (equality.Count == 0 && order.Count == 0) return null;   // a plain collection scan: the built-in index serves it

        if (_indexes.Any(i => i.Kind == IndexKind.Ordered && i.CollectionId == id && i.CollectionGroup == query.CollectionGroup
                              && Serves(i, equality, order)))
            return null;

        var fields = equality.Select(e => $"new(\"{e}\")")
            .Concat(order.Select(o => o.Direction == SortDirection.Desc ? $"new(\"{o.Path}\", SortDirection.Desc)" : $"new(\"{o.Path}\")"));
        return new IndexSuggestion(id,
            [.. equality.Select(e => new IndexField(e)), .. order.Select(o => new IndexField(o.Path, o.Direction))], IndexKind.Ordered, query.CollectionGroup,
            $"ib.{(query.CollectionGroup ? "AddGroup" : "Add")}(\"{id}\", [{string.Join(", ", fields)}])");
    }

    // An index serves the query when it starts with the equality fields (any order), then the order fields
    // in order, all in the same directions or all reversed (a btree reads either way).
    private static bool Serves(IndexDefinition index, List<string> equality, List<(string Path, SortDirection Direction)> order)
    {
        if (index.Fields.Count < equality.Count + order.Count) return false;
        if (!index.Fields.Take(equality.Count).Select(f => f.Path).ToHashSet(StringComparer.Ordinal).SetEquals(equality)) return false;
        bool? reversed = null;
        for (var i = 0; i < order.Count; i++)
        {
            var field = index.Fields[equality.Count + i];
            if (field.Path != order[i].Path) return false;
            var flip = field.Direction != order[i].Direction;
            if (reversed is { } r && r != flip) return false;
            reversed = flip;
        }
        return true;
    }
}

/// <summary>
/// The condition an array-membership index serves, added next to an array filter on an indexed field:
/// <c>winche_array_keys(field) @&gt; / &amp;&amp; ARRAY[winche_key(value), …]</c>. SQL only (never evaluated in memory).
/// </summary>
internal sealed record ArrayKeysFilter(FieldPath Field, FilterOperator Op, IReadOnlyList<Values.Value> Values) : Filter;

/// <summary>An index a slow query would have used, with the code to declare it.</summary>
public sealed record IndexSuggestion(string CollectionId, IReadOnlyList<IndexField> Fields, IndexKind Kind, bool CollectionGroup, string Code,
    long ElapsedMs = 0)
{
    public IndexDefinition ToDefinition() => new(CollectionId, Fields, Kind: Kind, CollectionGroup: CollectionGroup);
}
