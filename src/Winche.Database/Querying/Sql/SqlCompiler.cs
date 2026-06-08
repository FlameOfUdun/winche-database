using System.Text;
using Winche.Database.Constants;
using Winche.Database.Querying.Planning;

namespace Winche.Database.Querying.Sql;

/// <summary>
/// LogicalPlan → CompiledSql. Phase 2 compiles the linear query shape
/// [Scan, Filter?, Sort, CursorRange?, Page] into ONE flat SELECT.
/// Pipeline nodes (Group, Project, …) arrive in Phase 3 with CTE chaining.
/// </summary>
internal static class SqlCompiler
{
    private const string Alias = "d";
    private const string Columns = "path, id, collection, data, created_at, updated_at, version";

    public static CompiledSql Compile(LogicalPlan plan)
    {
        CollectionScan? scan = null;
        FilterNode? filter = null;
        SortNode? sort = null;
        CursorRangeNode? range = null;
        PageNode? page = null;

        foreach (var node in plan.Nodes)
        {
            switch (node)
            {
                case CollectionScan s: scan = s; break;
                case FilterNode f: filter = f; break;
                case SortNode so: sort = so; break;
                case CursorRangeNode r: range = r; break;
                case PageNode p: page = p; break;
                default: throw new NotSupportedException($"Phase 2 cannot compile {node.GetType().Name}.");
            }
        }

        if (scan is null || sort is null || page is null)
            throw new InvalidOperationException("Plan must contain Scan, Sort and Page nodes (Normalizer guarantees this).");

        var bag = new ParameterBag();
        var sb = new StringBuilder();

        sb.AppendLine($"SELECT {string.Join(", ", Columns.Split(", ").Select(c => $"{Alias}.{c}"))}");
        sb.AppendLine($"FROM {WincheTables.Documents} {Alias}");
        sb.AppendLine($"WHERE {Alias}.collection = {bag.Add(scan.Collection)}");

        if (filter is not null)
            sb.AppendLine($"  AND ({OperatorRegistry.Emit(filter.Predicate, bag, Alias)})");

        if (range is not null && CursorSql.Build(range, sort.Keys, bag, Alias) is { } cursor)
            sb.AppendLine($"  AND {cursor}");

        sb.AppendLine($"ORDER BY {OrderingSql.Build(sort.Keys, bag, Alias)}");

        var effectiveLimit = page.FetchExtraRow && page.Limit < int.MaxValue ? page.Limit + 1 : page.Limit;
        sb.Append($"LIMIT {bag.Add(effectiveLimit)}");
        if (page.Skip > 0)
            sb.Append($" OFFSET {bag.Add(page.Skip)}");

        return new CompiledSql(sb.ToString(), bag.ToArray());
    }
}
