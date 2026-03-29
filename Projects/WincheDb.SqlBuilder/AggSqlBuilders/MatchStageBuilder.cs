using WincheDb.Core.Ast;
using WincheDb.SqlBuilder.Infrastructure;
using WincheDb.SqlBuilder.QuerySqlBuilders;

namespace WincheDb.SqlBuilder.AggSqlBuilders;

internal static class MatchStageBuilder
{
    public static string Build(MatchStage stage, string prevAlias, ParameterBag bag)
    {
        var filter = new FilterSqlBuilder(prevAlias, bag).Build(stage.Filter);
        return $"SELECT * FROM {prevAlias} WHERE {prevAlias}.collection = {bag.Add(stage.Collection)} AND {filter}";
    }
}