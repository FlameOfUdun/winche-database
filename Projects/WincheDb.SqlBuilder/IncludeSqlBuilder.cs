using System.Text;
using WincheDb.Core.Ast;
using WincheDb.SqlBuilder;
using WincheDb.SqlBuilder.Infrastructure;

internal sealed class IncludeSqlBuilder(string table, ParameterBag bag)
{
    private int _depth;

    internal (string dataExpr, string lateralJoins) Build(List<IncludeQuery> includes, string parentAlias)
    {
        if (includes.Count == 0)
            return ($"{parentAlias}.data", "");

        var laterals = new StringBuilder();
        var mergeArgs = new List<string>();

        foreach (var include in includes)
        {
            var (aggAlias, lateralSql) = BuildInclude(include, parentAlias);
            laterals.Append(lateralSql);
            mergeArgs.Add($"'{include.Field}', COALESCE({aggAlias}.items, '[]'::jsonb)");
        }

        var enrichedData = $"{parentAlias}.data || jsonb_build_object({string.Join(", ", mergeArgs)})";
        return (enrichedData, laterals.ToString());
    }

    private (string aggAlias, string lateralSql) BuildInclude(IncludeQuery include, string parentAlias)
    {
        var depth = _depth++;
        var innerAlias = $"e{depth}";
        var innerSubAlias = $"e{depth}_inner";
        var aggAlias = $"e{depth}_agg";

        var innerSql = BuildInnerSubquery(include, parentAlias, innerSubAlias);

        var (nestedDataExpr, nestedLaterals) = Build(include.Include, innerAlias);

        var fullDocExpr = FullDocumentExpr(innerAlias, nestedDataExpr);

        var sb = new StringBuilder();
        sb.AppendLine($"LEFT JOIN LATERAL (");
        sb.AppendLine($"  SELECT jsonb_agg({fullDocExpr}) as items");
        sb.AppendLine($"  FROM ({innerSql}) {innerAlias}");
        sb.Append(nestedLaterals);
        sb.AppendLine($") {aggAlias} ON true");

        return (aggAlias, sb.ToString());
    }

    private string BuildInnerSubquery(IncludeQuery include, string parentAlias, string alias)
    {
        var sb = new StringBuilder();

        sb.Append($"SELECT * FROM {table} {alias}");
        sb.Append($" WHERE {alias}.collection = {parentAlias}.path || '/' || {bag.Add(include.Collection)}");

        var cursorBuilder = new CursorSqlBuilder(alias, bag);

        string? startCursor = null;
        string? endCursor = null;

        if (include.StartAfter.Count > 0)
            startCursor = cursorBuilder.Build(include.StartAfter, include.OrderBy, CursorBound.StartAfter);
        if (include.StartAt.Count > 0)
            startCursor = cursorBuilder.Build(include.StartAt, include.OrderBy, CursorBound.StartAt);
        if (include.EndAt.Count > 0)
            endCursor = cursorBuilder.Build(include.EndAt, include.OrderBy, CursorBound.EndAt);
        if (include.EndBefore.Count > 0)
            endCursor = cursorBuilder.Build(include.EndBefore, include.OrderBy, CursorBound.EndBefore);

        if (startCursor is not null)
            sb.Append($" AND {startCursor}");
        if (endCursor is not null)
            sb.Append($" AND {endCursor}");

        if (include.Where is not null)
        {
            var where = new FilterSqlBuilder(alias, bag).Build(include.Where);
            sb.Append($" AND ({where})");
        }

        if (include.OrderBy.Count > 0)
            sb.Append($" ORDER BY {OrderBySqlBuilder.Build(include.OrderBy, alias)}");

        sb.Append($" LIMIT {bag.Add(include.Limit)}");

        return sb.ToString();
    }

    private static string FullDocumentExpr(string alias, string dataExpr)
    {
        return $"jsonb_build_object(" +
           $"'id', {alias}.id, " +
           $"'path', {alias}.path, " +
           $"'collection', {alias}.collection, " +
           $"'createdAt', {alias}.created_at AT TIME ZONE 'UTC', " +
           $"'updatedAt', {alias}.updated_at AT TIME ZONE 'UTC', " +
           $"'version', {alias}.version, " +
           $"'data', {dataExpr})";
    }
}