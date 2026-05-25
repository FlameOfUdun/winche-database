using Winche.Database.AST.Models;

namespace Winche.Database.SQL.AggSqlBuilders;

internal static class LimitStageBuilder
{
    public static string Build(LimitStage stage, string prevAlias)
        => $"SELECT * FROM {prevAlias} LIMIT {stage.Count}";
}