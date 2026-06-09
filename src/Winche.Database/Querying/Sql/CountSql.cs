using System.Text;
using Winche.Database.Constants;
using Winche.Database.Querying.Planning;

namespace Winche.Database.Querying.Sql;

/// <summary>
/// LogicalPlan → COUNT(*) SQL. Shares the WHERE assembly (collection + scope regexes +
/// filter + cursor range) with <see cref="SqlCompiler"/>, but drops the document-column
/// projection and ORDER BY (neither affects a count). When the query carried an EXPLICIT
/// limit the count is capped at it (Firestore <c>count()</c> semantics) by counting the rows
/// of a LIMITed row-stream; an absent limit counts the full match — the Normalizer's default
/// page size (100) does NOT apply here.
/// </summary>
internal static class CountSql
{
    private const string Alias = "d";

    public static CompiledSql Compile(LogicalPlan plan, int? limit, IReadOnlyList<string>? scopeRegexes = null)
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
                case PageNode: break;          // count ignores paging; explicit limit handled below
                default: throw new NotSupportedException($"CountSql cannot compile {node.GetType().Name}.");
            }
        }

        if (scan is null || sort is null)
            throw new InvalidOperationException("Plan must contain Scan and Sort nodes (Normalizer guarantees this).");

        var bag = new ParameterBag();
        var where = new StringBuilder($"{Alias}.collection = {bag.Add(scan.Collection)}");
        foreach (var rx in scopeRegexes ?? [])
            where.Append($" AND {Alias}.collection ~ '{rx.Replace("'", "''")}'");
        if (filter is not null)
            where.Append($" AND ({OperatorRegistry.Emit(filter.Predicate, bag, Alias)})");
        if (range is not null && CursorSql.Build(range, sort.Keys, bag, Alias) is { } cursor)
            where.Append($" AND {cursor}");

        // An explicit limit caps the count: count rows of a LIMITed projection. Otherwise count the lot.
        var sql = limit is { } n
            ? $"SELECT COUNT(*) FROM (SELECT 1 FROM {WincheTables.Documents} {Alias} WHERE {where} LIMIT {bag.Add(n)}) _c"
            : $"SELECT COUNT(*) FROM {WincheTables.Documents} {Alias} WHERE {where}";

        return new CompiledSql(sql, bag.ToArray());
    }
}
