using WincheDatabase.AST.Models;
using WincheDatabase.SQL.FieldMapping;
using WincheDatabase.SQL.Infrastructure;

namespace WincheDatabase.SQL.QuerySqlBuilders;

internal sealed class CursorSqlBuilder(string alias, ParameterBag bag)
{
    internal string Build(List<object?> values, List<SortNode> fields, CursorBound boundary)
    {
        if (values.Count == 0 || fields.Count == 0) return "TRUE";

        var count = Math.Min(values.Count, fields.Count);
        var levels = Enumerable.Range(0, count)
            .Select(i => BuildLevel(values, fields, boundary, i, count));

        return $"({string.Join(" OR ", levels)})";
    }

    private string BuildLevel(List<object?> values, List<SortNode> fields, CursorBound boundary, int level, int count)
    {
        var parts = new List<string>();

        for (var i = 0; i < level; i++)
        {
            var expr = GetExpression(fields[i]);
            parts.Add(values[i] is null
                ? $"{expr} IS NULL"
                : $"{expr} = {bag.Add(values[i])}");
        }

        var currentExpr = GetExpression(fields[level]);
        var isLast = level == count - 1;
        var op = GetOperator(fields[level].Direction, boundary, isLast);

        parts.Add(values[level] is null
            ? (op is "=" or ">=" or "<=" ? $"{currentExpr} IS NULL" : "FALSE")
            : $"{currentExpr} {op} {bag.Add(values[level])}");

        return parts.Count == 1
            ? parts[0]
            : $"({string.Join(" AND ", parts)})";
    }

    private string GetExpression(SortNode sf)
    {
        var field = sf.Type.HasValue
            ? FieldResolver.Resolve(sf.Field, sf.Type.Value, alias)
            : FieldResolver.Resolve(sf.Field, alias);
        return FieldExpressionBuilder.CastExpression(field);
    }


    private static string GetOperator(SortDirection dir, CursorBound boundary, bool isLast) =>
        (boundary, dir, isLast) switch
        {
            (CursorBound.StartAfter, SortDirection.Asc, _) => ">",
            (CursorBound.StartAfter, SortDirection.Desc, _) => "<",
            (CursorBound.StartAt, SortDirection.Asc, true) => ">=",
            (CursorBound.StartAt, SortDirection.Asc, false) => ">",
            (CursorBound.StartAt, SortDirection.Desc, true) => "<=",
            (CursorBound.StartAt, SortDirection.Desc, false) => "<",
            (CursorBound.EndBefore, SortDirection.Asc, _) => "<",
            (CursorBound.EndBefore, SortDirection.Desc, _) => ">",
            (CursorBound.EndAt, SortDirection.Asc, true) => "<=",
            (CursorBound.EndAt, SortDirection.Asc, false) => "<",
            (CursorBound.EndAt, SortDirection.Desc, true) => ">=",
            (CursorBound.EndAt, SortDirection.Desc, false) => ">",
            _ => "="
        };
}