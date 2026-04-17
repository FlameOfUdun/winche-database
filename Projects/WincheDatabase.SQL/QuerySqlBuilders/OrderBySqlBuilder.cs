using WincheDatabase.AST.Models;
using WincheDatabase.SQL.FieldMapping;

namespace WincheDatabase.SQL.QuerySqlBuilders;

internal class OrderBySqlBuilder
{
    internal static string Build(List<SortNode> fields, string alias = "d")
    {
        var resolved = EnsureTiebreaker(fields);

        var parts = resolved.Select(sf =>
        {
            var dir = sf.Direction == SortDirection.Desc ? "DESC" : "ASC";

            var castType = sf.Type ?? (FieldResolver.Resolve(sf.Field, alias).IsJsonb
                ? FieldType.Numeric
                : FieldType.Text);

            var expr = FieldExpressionBuilder.CastExpression(
                FieldResolver.Resolve(sf.Field, castType, alias));

            return $"{expr} {dir}";
        });

        return string.Join(", ", parts);
    }

    private static List<SortNode> EnsureTiebreaker(List<SortNode> fields)
    {
        var hasId = fields.Any(f => string.Equals(f.Field, "id", StringComparison.OrdinalIgnoreCase));
        if (!hasId)
        {
            fields.Add(new SortNode("id", SortDirection.Desc));
        }

        var hasCreatedAt = fields.Any(f => string.Equals(f.Field, "created_at", StringComparison.OrdinalIgnoreCase));
        if (!hasCreatedAt) 
        { 
            fields.Add(new SortNode("created_at", SortDirection.Desc, FieldType.Timestamp));
        }

        var hasUpdatedAt = fields.Any(f => string.Equals(f.Field, "updated_at", StringComparison.OrdinalIgnoreCase));
        if (!hasUpdatedAt) 
        { 
            fields.Add(new SortNode("updated_at", SortDirection.Desc, FieldType.Timestamp));
        }

        return fields;
    }
}