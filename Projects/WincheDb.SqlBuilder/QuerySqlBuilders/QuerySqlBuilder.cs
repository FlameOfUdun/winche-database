using System.Text;
using WincheDb.Core.Ast;
using WincheDb.SqlBuilder.Infrastructure;

namespace WincheDb.SqlBuilder.QuerySqlBuilders;

public sealed class QuerySqlBuilder(string table = "documents")
{
    private const string Alias = "d";

    public SqlBuildResult Build(Query q)
    {
        var bag = new ParameterBag();
        var sb = new StringBuilder();

        // SELECT
        sb.AppendLine($"SELECT *");

        // FROM
        sb.AppendLine($"FROM {table} {Alias}");

        // WHERE
        sb.AppendLine($"WHERE {Alias}.collection = {bag.Add(q.Collection)}");

        var (startCursor, endCursor) = BuildCursors(q, bag);
        if (startCursor is not null)
            sb.AppendLine($"  AND {startCursor}");
        if (endCursor is not null) 
            sb.AppendLine($"  AND {endCursor}");

        if (q.Where is not null)
        {
            var where = new FilterSqlBuilder(Alias, bag).Build(q.Where);
            sb.AppendLine($"  AND ({where})");
        }

        // ORDER BY
        if (q.OrderBy.Count > 0)
            sb.AppendLine($"ORDER BY {OrderBySqlBuilder.Build(q.OrderBy, Alias)}");

        // LIMIT — fetch one extra row to detect if more results exist
        sb.AppendLine($"LIMIT {bag.Add(q.Limit + 1)}");

        return new SqlBuildResult(sb.ToString().Trim(), bag.ToArray());
    }

    private static (string? start, string? end) BuildCursors(Query q, ParameterBag bag)
    {
        var builder = new CursorSqlBuilder(Alias, bag);

        string? start = null;
        string? end = null;

        // Pick the more specific start cursor (StartAt takes priority over StartAfter)
        if (q.StartAfter.Count > 0)
            start = builder.Build(q.StartAfter, q.OrderBy, CursorBound.StartAfter);
        if (q.StartAt.Count > 0)
            start = builder.Build(q.StartAt, q.OrderBy, CursorBound.StartAt);

        // Pick the more specific end cursor (EndBefore takes priority over EndAt)
        if (q.EndAt.Count > 0)
            end = builder.Build(q.EndAt, q.OrderBy, CursorBound.EndAt);
        if (q.EndBefore.Count > 0)
            end = builder.Build(q.EndBefore, q.OrderBy, CursorBound.EndBefore);

        return (start, end);
    }
}