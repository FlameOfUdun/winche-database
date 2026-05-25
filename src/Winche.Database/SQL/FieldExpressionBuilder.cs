using System.Text;
using Winche.Database.AST.Models;
using Winche.Database.SQL.FieldMapping;

namespace Winche.Database.SQL;

internal static class FieldExpressionBuilder
{
    internal static string Accessor(ResolvedField f) =>
        f.IsColumn ? ColumnExpr(f) : BuildJsonbAccessor(f);

    internal static string Expression(ResolvedField f) =>
        f.IsColumn ? ColumnExpr(f) : $"({BuildJsonbAccessor(f)})";

    internal static string CastExpression(ResolvedField f)
    {
        if (f.IsColumn) return ColumnExpr(f);
        if (f.Cast is FieldType.Text) return Expression(f);
        if (f.Cast is FieldType.Jsonb) return $"({BuildJsonbAccessor(f)})::jsonb";
        if (f.Cast is FieldType.Timestamp) return $"public.parse_timestamp({BuildJsonbAccessor(f)})";

        return $"{Expression(f)}::{ToSqlCast(f.Cast)}";
    }

    internal static string IndexExpression(ResolvedField f) =>
        f.IsColumn ? ColumnExpr(f) : $"({CastExpression(f)})";

    internal static string ToSqlCast(FieldType type) => type switch
    {
        FieldType.Text => "text",
        FieldType.Integer => "integer",
        FieldType.BigInt => "bigint",
        FieldType.Numeric => "numeric",
        FieldType.Double => "double precision",
        FieldType.Boolean => "boolean",
        FieldType.Timestamp => "timestamptz",
        FieldType.Date => "date",
        FieldType.Uuid => "uuid",
        FieldType.Jsonb => "jsonb",
        _ => "text"
    };

    private static string ColumnExpr(ResolvedField f) =>
        f.Alias is null ? f.Path : $"{f.Alias}.{f.Path}";

    private static string BuildJsonbAccessor(ResolvedField f)
    {
        var prefix = f.Alias is null ? "data" : $"{f.Alias}.data";
        var segments = f.Path.Split('.');
        var useArrow = f.Cast == FieldType.Jsonb;

        var sb = new StringBuilder(prefix);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            sb.Append($"->'{segments[i]}'");
        }
        sb.Append(useArrow ? $"->'{segments[^1]}'" : $"->>'{segments[^1]}'");
        return sb.ToString();
    }
}