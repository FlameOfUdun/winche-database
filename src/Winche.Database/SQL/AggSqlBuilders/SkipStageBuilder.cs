using Winche.Database.AST.Models;

namespace Winche.Database.SQL.AggSqlBuilders;

internal static class SkipStageBuilder
{
    public static string Build(SkipStage stage, string prevAlias)
        => $"SELECT * FROM {prevAlias} OFFSET {stage.Count}";
}