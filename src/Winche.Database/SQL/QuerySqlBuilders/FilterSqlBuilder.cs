using Winche.Database.AST.Models;
using Winche.Database.SQL;
using Winche.Database.SQL.FieldMapping;
using Winche.Database.SQL.Infrastructure;

namespace Winche.Database.SQL.QuerySqlBuilders;

internal class FilterSqlBuilder(string alias = "d", ParameterBag? bag = null)
{
    protected readonly string _alias = alias;
    protected readonly ParameterBag _bag = bag ?? new();

    internal string Build(WhereNode node) => node switch
    {
        FieldFilter fc => BuildFieldFilter(fc),
        LogicGroup lg => BuildLogicGroup(lg),
        FieldCompare cm => BuildFieldCompare(cm),
        _ => "TRUE"
    };

    // ── Virtual hook — HavingFilterSqlBuilder intercepts aggregate aliases ────

    protected virtual string? OverrideField(string path) => null;

    // ── FieldFilter ───────────────────────────────────────────────────────────

    private string BuildFieldFilter(FieldFilter fc)
    {
        var overridden = OverrideField(fc.Field);
        if (overridden is not null)
            return BuildWithExpr(overridden, fc.Operator, fc.Value);

        var field = FieldResolver.Resolve(fc.Field, _alias);

        if (field.IsJsonb && fc.Type is null)
            throw new InvalidOperationException(
                $"Field '{fc.Field}' is a JSONB path. A 'type' must be specified in the FieldFilter.");

        var typedField = fc.Type.HasValue ? field.WithCast(fc.Type.Value) : field;

        var expr = FieldExpressionBuilder.Expression(field);
        var castExpr = FieldExpressionBuilder.CastExpression(typedField);
        var accessor = FieldExpressionBuilder.Accessor(field);

        return fc.Operator switch
        {
            ConditionalOperator.Eq when fc.Value is null => $"{expr} IS NULL",
            ConditionalOperator.Eq => $"{castExpr} = {_bag.Add(fc.Value)}",
            ConditionalOperator.Ne when fc.Value is null => $"{expr} IS NOT NULL",
            ConditionalOperator.Ne => $"({expr} IS NULL OR {castExpr} <> {_bag.Add(fc.Value)})",
            ConditionalOperator.Gt => $"{castExpr} > {_bag.Add(fc.Value)}",
            ConditionalOperator.Gte => $"{castExpr} >= {_bag.Add(fc.Value)}",
            ConditionalOperator.Lt => $"{castExpr} < {_bag.Add(fc.Value)}",
            ConditionalOperator.Lte => $"{castExpr} <= {_bag.Add(fc.Value)}",
            ConditionalOperator.In => $"{expr} = ANY({_bag.Add(fc.Value)})",
            ConditionalOperator.Nin => $"{expr} <> ALL({_bag.Add(fc.Value)})",
            ConditionalOperator.Contains => $"{expr} ILIKE {_bag.Add($"%{fc.Value}%")}",
            ConditionalOperator.StartsWith => $"{expr} ILIKE {_bag.Add($"{fc.Value}%")}",
            ConditionalOperator.EndsWith => $"{expr} ILIKE {_bag.Add($"%{fc.Value}")}",
            ConditionalOperator.Regex => $"{expr} ~ {_bag.Add(fc.Value)}",
            ConditionalOperator.ArrContains => $"{accessor} @> {_bag.Add(fc.Value)}::jsonb",
            ConditionalOperator.ArrContainsAny => $"{accessor} && {_bag.Add(fc.Value)}::jsonb",
            ConditionalOperator.ArrContainsAll => $"{accessor} @> {_bag.Add(fc.Value)}::jsonb",
            ConditionalOperator.Exists when fc.Value is true => $"{expr} IS NOT NULL",
            ConditionalOperator.Exists => $"{expr} IS NULL",
            _ => "TRUE"
        };
    }

    // Builds a condition against a pre-resolved SQL expression (used by OverrideField)
    private string BuildWithExpr(string expr, ConditionalOperator op, object? value) => op switch
    {
        ConditionalOperator.Eq when value is null => $"{expr} IS NULL",
        ConditionalOperator.Eq => $"{expr} = {_bag.Add(value)}",
        ConditionalOperator.Ne when value is null => $"{expr} IS NOT NULL",
        ConditionalOperator.Ne => $"({expr} IS NULL OR {expr} <> {_bag.Add(value)})",
        ConditionalOperator.Gt => $"{expr} > {_bag.Add(value)}",
        ConditionalOperator.Gte => $"{expr} >= {_bag.Add(value)}",
        ConditionalOperator.Lt => $"{expr} < {_bag.Add(value)}",
        ConditionalOperator.Lte => $"{expr} <= {_bag.Add(value)}",
        ConditionalOperator.In => $"{expr} = ANY({_bag.Add(value)})",
        ConditionalOperator.Nin => $"{expr} <> ALL({_bag.Add(value)})",
        _ => "TRUE"
    };

    // ── LogicGroup ────────────────────────────────────────────────────────────

    private string BuildLogicGroup(LogicGroup lg)
    {
        if (lg.Children.Count == 0) return "TRUE";

        var parts = lg.Children.Select(Build).ToList();

        return lg.Operator switch
        {
            LogicalOperator.And => $"({string.Join(" AND ", parts)})",
            LogicalOperator.Or => $"({string.Join(" OR ", parts)})",
            LogicalOperator.Not when parts.Count == 1 => $"NOT ({parts[0]})",
            _ => "TRUE"
        };
    }

    // ── FieldCompare ──────────────────────────────────────────────────────────

    private string BuildFieldCompare(FieldCompare cm)
    {
        var castType = cm.Type ?? FieldType.Text;
        var left = FieldExpressionBuilder.CastExpression(FieldResolver.Resolve(cm.Left, castType, _alias));
        var right = FieldExpressionBuilder.CastExpression(FieldResolver.Resolve(cm.Right, castType, _alias));
        var op = ToSqlOp(cm.Operator);

        return $"{left} {op} {right}";
    }

    private static string ToSqlOp(ConditionalOperator op) => op switch
    {
        ConditionalOperator.Eq => "=",
        ConditionalOperator.Ne => "<>",
        ConditionalOperator.Gt => ">",
        ConditionalOperator.Gte => ">=",
        ConditionalOperator.Lt => "<",
        ConditionalOperator.Lte => "<=",
        _ => "="
    };
}