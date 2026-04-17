using WincheDatabase.AST.Models;
using WincheDatabase.SQL.QuerySqlBuilders;

namespace WincheDatabase.SQL.AggSqlBuilders;

internal static class SortStageBuilder
{
    public static string Build(
        SortStage stage,
        string prevAlias,
        bool addTiebreaker = false,
        bool isPostTransformation = false)
    {
        string orderBy;

        if (isPostTransformation)
        {
            // After GroupStage/ProjectStage — fields are projected column aliases,
            // not document columns or JSONB paths. Quote them directly.
            var parts = stage.Fields.Select(f =>
            {
                var dir = f.Direction == SortDirection.Desc ? "DESC" : "ASC";
                return $"\"{f.Field}\" {dir}";
            });
            orderBy = string.Join(", ", parts);
        }
        else
        {
            var fields = addTiebreaker ? stage.Fields : [.. stage.Fields];
            orderBy = OrderBySqlBuilder.Build(fields, prevAlias);
        }

        return $"SELECT * FROM {prevAlias} ORDER BY {orderBy}";
    }
}