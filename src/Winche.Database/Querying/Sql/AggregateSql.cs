using System.Text;
using Winche.Database.Constants;
using Winche.Database.Querying.Planning;

namespace Winche.Database.Querying.Sql;

/// <summary>
/// LogicalPlan + aggregations → a single-row aggregate SQL. Reuses the CountSql WHERE assembly
/// (collection + scope + filter + cursor), projects per-row values in an inner (optionally
/// LIMIT-capped) subquery, then aggregates. Column layout per aggregation: Count=1 (COUNT(*)),
/// Sum=3 (exact-integer numeric sum, double-presence bool, NaN-safe float8 sum), Average=1 (float8).
/// </summary>
internal static class AggregateSql
{
    private const string Alias = "d";

    public static CompiledSql Compile(LogicalPlan plan, IReadOnlyList<Aggregation> aggregations, int? limit, string? collectionIdScope = null)
    {
        CollectionScan? scan = null;
        FilterNode? filter = null;
        SortNode? sort = null;
        CursorRangeNode? range = null;

        foreach (var node in plan.Nodes)
        {
            switch (node)
            {
                case CollectionScan s: scan = s; break;
                case FilterNode f: filter = f; break;
                case SortNode so: sort = so; break;
                case CursorRangeNode r: range = r; break;
                case PageNode: break;
                default: throw new NotSupportedException($"AggregateSql cannot compile {node.GetType().Name}.");
            }
        }

        if (scan is null || sort is null)
            throw new InvalidOperationException("Plan must contain Scan and Sort nodes (Normalizer guarantees this).");

        var bag = new ParameterBag();

        var where = new StringBuilder($"{Alias}.collection_path = {bag.Add(scan.Collection)}");
        if (collectionIdScope is not null)
            where.Append($" AND {Alias}.collection_id = {bag.Add(collectionIdScope)}");
        if (filter is not null)
            where.Append($" AND ({OperatorRegistry.Emit(filter.Predicate, bag, Alias)})");
        if (range is not null && CursorSql.Build(range, sort.Keys, bag, Alias) is { } cursor)
            where.Append($" AND {cursor}");

        var inner = new List<string> { "1 AS _r" };   // ensure the subquery has at least one column for COUNT
        var outer = new List<string>();

        for (var i = 0; i < aggregations.Count; i++)
        {
            var a = aggregations[i];
            switch (a.Kind)
            {
                case AggregateKind.Count:
                    outer.Add("COUNT(*)");
                    break;

                case AggregateKind.Sum:
                {
                    var tagged = FieldAccessSql.Tagged(a.Field!, bag, Alias);
                    inner.Add($"winche_agg_num({tagged}) AS sv{i}");
                    inner.Add($"({tagged} ? 'integerValue') AS si{i}");
                    inner.Add($"({tagged} ? 'doubleValue') AS sd{i}");
                    outer.Add($"COALESCE(SUM(sv{i}) FILTER (WHERE si{i}), 0)"); // exact integer sum (numeric)
                    outer.Add($"COALESCE(BOOL_OR(sd{i}), false)");              // any double operand?
                    outer.Add($"COALESCE(SUM(sv{i})::float8, 0)");              // full sum as double (NaN-safe)
                    break;
                }

                case AggregateKind.Average:
                {
                    var tagged = FieldAccessSql.Tagged(a.Field!, bag, Alias);
                    inner.Add($"winche_agg_num({tagged}) AS av{i}");
                    outer.Add($"AVG(av{i})::float8");                           // double or NULL (empty)
                    break;
                }
            }
        }

        var limitClause = limit is { } n ? $" LIMIT {bag.Add(n)}" : "";
        var sql =
            $"SELECT {string.Join(", ", outer)} " +
            $"FROM (SELECT {string.Join(", ", inner)} FROM {WincheTables.Documents} {Alias} WHERE {where}{limitClause}) _agg";

        return new CompiledSql(sql, bag.ToArray());
    }
}
