using WincheDb.Core.Ast;
using WincheDb.SqlBuilder.FieldMapping;
using WincheDb.SqlBuilder.Infrastructure;
using WincheDb.SqlBuilder.QuerySqlBuilders;

namespace WincheDb.SqlBuilder.AggSqlBuilders;

internal static class LookupStageBuilder
{
    public static string Build(LookupStage stage, string prevAlias, string tableName, ParameterBag bag)
    {
        var foreignAlias = "f";
        var localExpr = ResolveExpr(stage.LocalField, prevAlias);
        var foreignExpr = ResolveExpr(stage.ForeignField, foreignAlias);
        var subSelect = BuildSubSelect(stage, foreignAlias, localExpr, foreignExpr, tableName, bag);

        return $"SELECT {prevAlias}.*, ({subSelect}) AS \"{stage.As}\" FROM {prevAlias}";
    }

    private static string BuildSubSelect(LookupStage stage, string foreignAlias, string localExpr, string foreignExpr, string tableName, ParameterBag bag)
    {
        var whereParts = new List<string>
        {
            $"{foreignAlias}.collection = {bag.Add(stage.Collection)}",
            $"{foreignExpr} = {localExpr}"
        };

        if (stage.Filter is not null)
        {
            var filterSql = new FilterSqlBuilder(foreignAlias, bag).Build(stage.Filter);
            whereParts.Add(filterSql);
        }

        var whereClause = string.Join(" AND ", whereParts);

        var orderByClause = stage.OrderBy is { Count: > 0 }
            ? $" ORDER BY {OrderBySqlBuilder.Build(stage.OrderBy, foreignAlias)}"
            : string.Empty;

        return $"SELECT jsonb_agg(to_jsonb({foreignAlias})) " +
           $"FROM {tableName} {foreignAlias} " +
           $"WHERE {whereClause}" +
           $"{orderByClause} " +
           $"LIMIT {stage.Limit}";
    }

    private static string ResolveExpr(string field, string alias)
    {
        var resolved = FieldResolver.Resolve(field, FieldType.Text, alias);
        return FieldExpressionBuilder.Expression(resolved);
    }
}