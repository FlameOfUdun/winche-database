using WincheDb.Core.Ast;

namespace WincheDb.SqlBuilder.AggSqlBuilders;

internal static class SkipStageBuilder
{
    public static string Build(SkipStage stage, string prevAlias)
        => $"SELECT * FROM {prevAlias} OFFSET {stage.Count}";
}