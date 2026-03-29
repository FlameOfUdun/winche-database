using WincheDb.Core.Ast;
using WincheDb.SqlBuilder.FieldMapping;
using WincheDb.SqlBuilder.Infrastructure;
using WincheDb.SqlBuilder.QuerySqlBuilders;

namespace WincheDb.SqlBuilder.AggSqlBuilders;

internal static class GroupStageBuilder
{
    public static string Build(GroupStage stage, string prevAlias, ParameterBag bag)
    {
        var selectParts = new List<string>();

        foreach (var key in stage.Keys)
        {
            var resolved = FieldResolver.Resolve(key.Field, key.Type ?? FieldType.Text, prevAlias);
            var expr = FieldExpressionBuilder.CastExpression(resolved);
            selectParts.Add($"{expr} AS \"{key.As}\"");
        }

        // build accumulator SQL expressions and keep a map for HAVING resolution
        var accumulatorSql = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var acc in stage.Accumulators)
        {
            var result = BuildAccumulator(acc, prevAlias);
            accumulatorSql[acc.As] = result;
            selectParts.Add($"{result} AS \"{acc.As}\"");
        }

        var selectClause = string.Join(", ", selectParts);
        var groupByClause = BuildGroupBy(stage.Keys, prevAlias);
        var sql = $"SELECT {selectClause} FROM {prevAlias} GROUP BY {groupByClause}";

        if (stage.Having is not null)
        {
            var havingFilter = new HavingFilterSqlBuilder(prevAlias, bag, accumulatorSql).Build(stage.Having);
            sql += $" HAVING {havingFilter}";
        }

        return sql;
    }

    private static string BuildAccumulator(AccumulatorField acc, string alias)
    {
        var fieldExpr = acc.Field is not null ? ResolveExpr(acc.Field, acc.Type, alias) : null;
        var numericExpr = acc.Field is not null ? ResolveNumeric(acc.Field, alias) : null;

        return acc.Function switch
        {
            AggFunction.Count => acc.Field is null ? "COUNT(*)" : $"COUNT({fieldExpr})",
            AggFunction.Sum => $"SUM({numericExpr})",
            AggFunction.Avg => $"AVG({numericExpr})",
            AggFunction.Min => $"MIN({fieldExpr})",
            AggFunction.Max => $"MAX({fieldExpr})",
            AggFunction.Push => $"jsonb_agg({fieldExpr})",
            AggFunction.AddToSet => $"jsonb_agg(DISTINCT {fieldExpr})",
            AggFunction.First => $"(array_agg({fieldExpr}))[1]",
            AggFunction.Last => $"(array_agg({fieldExpr}))[array_length(array_agg({fieldExpr}), 1)]",
            _ => "COUNT(*)"
        };
    }

    private static string BuildGroupBy(List<GroupKey> keys, string alias)
    {
        var parts = keys.Select(k =>
        {
            var resolved = FieldResolver.Resolve(k.Field, k.Type ?? FieldType.Text, alias);
            return FieldExpressionBuilder.CastExpression(resolved);
        });
        return string.Join(", ", parts);
    }

    private static string ResolveExpr(string field, FieldType? type, string alias)
    {
        var resolved = FieldResolver.Resolve(field, type ?? FieldType.Text, alias);
        return FieldExpressionBuilder.Expression(resolved);
    }

    private static string ResolveNumeric(string field, string alias)
    {
        var resolved = FieldResolver.Resolve(field, FieldType.Numeric, alias);
        return FieldExpressionBuilder.CastExpression(resolved);
    }
}

internal sealed class HavingFilterSqlBuilder(string alias, ParameterBag bag, IReadOnlyDictionary<string, string> accumulatorSql) : FilterSqlBuilder(alias, bag)
{
    private readonly IReadOnlyDictionary<string, string> _accumulatorSql = accumulatorSql;

    protected override string? OverrideField(string path)
        => _accumulatorSql.TryGetValue(path, out var sql) ? sql : null;
}