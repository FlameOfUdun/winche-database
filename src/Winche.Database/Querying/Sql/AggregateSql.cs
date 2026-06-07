// src/Winche.Database/Querying/Sql/AggregateSql.cs
using Winche.Database.Querying.Ast;

namespace Winche.Database.Querying.Sql;

/// <summary>
/// THE single accumulator emitter, shared by Group (grouped aggregates) and Project
/// (windowed aggregates). Every emission yields a TAGGED value jsonb:
///   count → integerValue; sum/avg → doubleValue (sum of nothing = 0, avg of nothing = nullValue);
///   min/max → winning tagged value by winche_key; push/addToSet → tagged arrayValue;
///   first/last → positional (incoming row order — only meaningful after a sort).
/// Windowed mode supports count/sum/avg only (Postgres window aggregates cannot take
/// FILTER or inner ORDER BY, which the others require) — enforced here and by PROJECT_AGG.
/// Non-numeric and NaN inputs are excluded from sum/avg (<c>winche_num</c> yields NULL);
/// sum over only such values = 0.
/// </summary>
internal static class AggregateSql
{
    internal static string Emit(AggFunction fn, string? tagged, bool windowed)
    {
        if (windowed && fn is not (AggFunction.Count or AggFunction.Sum or AggFunction.Avg))
            throw new NotSupportedException($"Windowed aggregates support count/sum/avg only, got {fn}.");

        var over = windowed ? " OVER ()" : "";
        return fn switch
        {
            AggFunction.Count => tagged is null
                ? $"jsonb_build_object('integerValue', (COUNT(*){over})::text)"
                : $"jsonb_build_object('integerValue', (COUNT({tagged}){over})::text)",

            AggFunction.Sum =>
                $"jsonb_build_object('doubleValue', (COALESCE(SUM(winche_num({tagged})){over}, 0))::float8)",

            AggFunction.Avg =>
                $"CASE WHEN AVG(winche_num({tagged})){over} IS NULL THEN '{{\"nullValue\":null}}'::jsonb " +
                $"ELSE jsonb_build_object('doubleValue', (AVG(winche_num({tagged})){over})::float8) END",

            AggFunction.Min =>
                $"(array_agg({tagged} ORDER BY winche_key({tagged})) FILTER (WHERE ({tagged}) IS NOT NULL))[1]",

            AggFunction.Max =>
                $"(array_agg({tagged} ORDER BY winche_key({tagged}) DESC) FILTER (WHERE ({tagged}) IS NOT NULL))[1]",

            AggFunction.Push =>
                $"jsonb_build_object('arrayValue', jsonb_build_object('values', " +
                $"COALESCE(jsonb_agg({tagged}) FILTER (WHERE ({tagged}) IS NOT NULL), '[]'::jsonb)))",

            AggFunction.AddToSet =>
                $"jsonb_build_object('arrayValue', jsonb_build_object('values', " +
                $"COALESCE(jsonb_agg(DISTINCT {tagged}) FILTER (WHERE ({tagged}) IS NOT NULL), '[]'::jsonb)))",

            AggFunction.First => $"(array_agg({tagged}))[1]",

            AggFunction.Last => $"(array_agg({tagged}))[cardinality(array_agg({tagged}))]",

            _ => throw new NotSupportedException($"Unknown aggregate function: {fn}"),
        };
    }
}
