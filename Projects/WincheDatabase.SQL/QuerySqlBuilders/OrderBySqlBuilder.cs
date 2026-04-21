using WincheDatabase.AST.Models;
using WincheDatabase.SQL.FieldMapping;

namespace WincheDatabase.SQL.QuerySqlBuilders;

internal class OrderBySqlBuilder
{
    internal static string Build(List<SortNode> fields, string? alias = "d", bool forIndex = false)
    {
        var resolved = EnsureTiebreaker(fields);

        var parts = resolved.Select(sf =>
        {
            var dir = sf.Direction == SortDirection.Desc ? "DESC" : "ASC";

            var castType = sf.Type ?? (FieldResolver.Resolve(sf.Field, alias).IsJsonb
                ? FieldType.Numeric
                : FieldType.Text);

            var resolvedField = FieldResolver.Resolve(sf.Field, castType, alias);
            var expr = forIndex
                ? FieldExpressionBuilder.IndexExpression(resolvedField)
                : FieldExpressionBuilder.CastExpression(resolvedField);

            return $"{expr} {dir}";
        });

        return string.Join(", ", parts);
    }

    private static List<SortNode> EnsureTiebreaker(List<SortNode> fields)
    {
        var copy = new List<SortNode>(fields);

        if (!copy.Any(f => string.Equals(f.Field, "id", StringComparison.OrdinalIgnoreCase)))
            copy.Add(new SortNode("id", SortDirection.Desc));

        if (!copy.Any(f => string.Equals(f.Field, "created_at", StringComparison.OrdinalIgnoreCase)))
            copy.Add(new SortNode("created_at", SortDirection.Desc, FieldType.Timestamp));

        if (!copy.Any(f => string.Equals(f.Field, "updated_at", StringComparison.OrdinalIgnoreCase)))
            copy.Add(new SortNode("updated_at", SortDirection.Desc, FieldType.Timestamp));

        return copy;
    }
}