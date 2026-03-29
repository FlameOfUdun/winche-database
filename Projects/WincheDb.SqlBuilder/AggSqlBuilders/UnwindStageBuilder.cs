using WincheDb.Core.Ast;
using WincheDb.SqlBuilder.FieldMapping;

namespace WincheDb.SqlBuilder.AggSqlBuilders;

internal static class UnwindStageBuilder
{
    public static string Build(UnwindStage stage, string prevAlias)
    {
        var resolved = FieldResolver.Resolve(stage.Field, FieldType.Jsonb, prevAlias);
        var fieldExpr = FieldExpressionBuilder.Accessor(resolved);

        var joinType = stage.PreserveNullAndEmpty ? "LEFT JOIN" : "CROSS JOIN";

        // Rebuild data: strip the original array key, inject each element under the As key
        var fieldKeyParam = $"'{stage.Field}'";
        var asKeyParam = $"'{stage.As}'";

        var mergedData =
            $"({prevAlias}.data - {fieldKeyParam} || " +
            $"jsonb_build_object({asKeyParam}, elem.value))";

        return
            $"SELECT {prevAlias}.id, {prevAlias}.path, {prevAlias}.collection, " +
            $"{prevAlias}.createdat, {prevAlias}.updatedat, {prevAlias}.version, " +
            $"{mergedData} AS data " +
            $"FROM {prevAlias} " +
            $"{joinType} LATERAL jsonb_array_elements({fieldExpr}) AS elem(value)";
    }
}