using WincheDatabase.AST.Models;

namespace WincheDatabase.SQL.AggSqlBuilders;

internal static class SkipStageBuilder
{
    public static string Build(SkipStage stage, string prevAlias)
        => $"SELECT * FROM {prevAlias} OFFSET {stage.Count}";
}