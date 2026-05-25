using Winche.Database.AST.Models;
using Winche.Database.SQL.Infrastructure;
using Winche.Database.SQL.QuerySqlBuilders;

namespace Winche.Database.SQL.AggSqlBuilders;

internal static class MatchStageBuilder
{
    public static string Build(MatchStage stage, string prevAlias, ParameterBag bag)
    {
        var filter = stage.Filter == null ? null : new FilterSqlBuilder(prevAlias, bag).Build(stage.Filter);
        return $"SELECT * FROM {prevAlias} WHERE {prevAlias}.collection = {bag.Add(stage.Collection)} AND {filter ?? "TRUE"}";
    }
}