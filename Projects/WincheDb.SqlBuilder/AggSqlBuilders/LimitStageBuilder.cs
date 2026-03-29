using WincheDb.Core.Ast;

namespace WincheDb.SqlBuilder.AggSqlBuilders;

internal static class LimitStageBuilder
{
    public static string Build(LimitStage stage, string prevAlias)
        => $"SELECT * FROM {prevAlias} LIMIT {stage.Count}";
}