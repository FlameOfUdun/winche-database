using System.Text;
using Winche.Database.Constants;
using Winche.Database.Documents;
using Winche.Database.Querying.Planning;

namespace Winche.Database.Querying.Sql;

/// <summary>
/// LogicalPlan → CompiledSql. Compiles the linear query shape
/// [Scan, Filter?, Sort, CursorRange?, Page] into ONE flat SELECT.
/// </summary>
internal static class SqlCompiler
{
    private const string Alias = "d";
    private const string Columns = "document_path, document_id, collection_path, collection_id, data, created_at, updated_at, version";

    public static CompiledSql Compile(
        LogicalPlan plan,
        string? collectionIdScope = null,
        IReadOnlyList<FieldPath>? select = null)
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

        // Build the SELECT column list; replace d.data with a JSONB projection when Select is provided.
        var dataColumn = select is { Count: > 0 }
            ? $"{ProjectionSql.Build(select, $"{Alias}.data", bag)} AS data"
            : $"{Alias}.data";

        var allColumns = Columns.Split(", ")
            .Select(c => c == "data" ? dataColumn : $"{Alias}.{c}");
        sb.AppendLine($"SELECT {string.Join(", ", allColumns)}");
        sb.AppendLine($"FROM {WincheTables.Documents} {Alias}");
        sb.AppendLine($"WHERE {Alias}.collection_path = {bag.Add(scan.Collection)}");
        if (collectionIdScope is not null)
            sb.AppendLine($"  AND {Alias}.collection_id = {bag.Add(collectionIdScope)}");

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
