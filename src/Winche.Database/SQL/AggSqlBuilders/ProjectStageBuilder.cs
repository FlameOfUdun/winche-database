using Winche.Database.AST.Models;
using Winche.Database.SQL;
using Winche.Database.SQL.FieldMapping;
using Winche.Database.SQL.Infrastructure;

namespace Winche.Database.SQL.AggSqlBuilders;

internal static class ProjectStageBuilder
{
    public static string Build(ProjectStage stage, string prevAlias, ParameterBag bag, bool isPostTransformation = false)
    {
        var selectParts = stage.Fields
            .Select(f => $"{BuildExpr(f.Expression, prevAlias, bag, isPostTransformation)} AS \"{f.As}\"");

        return $"SELECT {string.Join(", ", selectParts)} FROM {prevAlias}";
    }

    private static string BuildExpr(ProjectExpr expr, string alias, ParameterBag bag, bool isPostTransformation) => expr switch
    {
        FieldRefExpr fre => BuildFieldRef(fre, alias, isPostTransformation),
        LiteralExpr le => BuildLiteral(le, bag),
        AggFuncExpr afe => BuildWindowFunc(afe, alias, isPostTransformation),
        _ => throw new NotSupportedException($"Unsupported ProjectExpr: {expr.GetType().Name}")
    };

    private static string BuildFieldRef(FieldRefExpr fre, string alias, bool isPostTransformation)
    {
        if (isPostTransformation)
            return $"\"{fre.Field}\"";

        var resolved = FieldResolver.Resolve(fre.Field, fre.Type ?? FieldType.Text, alias);
        return FieldExpressionBuilder.CastExpression(resolved);
    }

    private static string BuildLiteral(LiteralExpr le, ParameterBag bag)
        => le.Value is null ? "NULL" : bag.Add(le.Value);

    private static string BuildWindowFunc(AggFuncExpr afe, string alias, bool isPostTransformation)
    {
        string? fieldExpr = null;
        if (afe.Field is not null)
        {
            fieldExpr = isPostTransformation
                ? $"\"{afe.Field}\""
                : FieldExpressionBuilder.Expression(
                    FieldResolver.Resolve(afe.Field, afe.Type ?? FieldType.Text, alias));
        }

        var aggSql = afe.Function switch
        {
            AggFunction.Count => fieldExpr is null ? "COUNT(*)" : $"COUNT({fieldExpr})",
            AggFunction.Sum => $"SUM({fieldExpr})",
            AggFunction.Avg => $"AVG({fieldExpr})",
            AggFunction.Min => $"MIN({fieldExpr})",
            AggFunction.Max => $"MAX({fieldExpr})",
            AggFunction.Push => $"jsonb_agg({fieldExpr})",
            AggFunction.AddToSet => $"jsonb_agg(DISTINCT {fieldExpr})",
            AggFunction.First => $"(array_agg({fieldExpr}))[1]",
            AggFunction.Last => $"(array_agg({fieldExpr}))[array_length(array_agg({fieldExpr}), 1)]",
            _ => "COUNT(*)"
        };

        return $"{aggSql} OVER ()";
    }
}