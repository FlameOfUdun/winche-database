using Winche.Database.AST.Models;
using Winche.Database.SQL;
using Winche.Database.SQL.FieldMapping;

namespace Winche.Database.SQL.QuerySqlBuilders;

internal class OrderBySqlBuilder
{
    internal static string Build(List<SortNode> fields, string? alias = "d", bool forIndex = false)
    {
        var resolved = forIndex ? fields : EnsureTiebreaker(fields);

        var parts = resolved.Select(sf =>
        {
            var dir = sf.Direction == SortDirection.Desc ? "DESC" : "ASC";

            var baseField = FieldResolver.Resolve(sf.Field, alias);

            if (baseField.IsJsonb && sf.Type is null)
                throw new InvalidOperationException(
                    $"Sort field '{sf.Field}' is a JSONB path. A 'type' must be specified in the SortNode.");

            var resolvedField = sf.Type.HasValue ? baseField.WithCast(sf.Type.Value) : baseField;
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

        return copy;
    }
}