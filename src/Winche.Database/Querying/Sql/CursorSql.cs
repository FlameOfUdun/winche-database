// src/Winche.Database/Querying/Sql/CursorSql.cs
using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Planning;
using Winche.Database.Values;

namespace Winche.Database.Querying.Sql;

/// <summary>
/// Cursor boundaries → tuple-expansion WHERE fragment, built on OperatorRegistry's
/// comparison fragments so cursors agree with filters and ORDER BY by construction.
///
/// Lower (after boundary B): OR over levels i: (key_0=B_0 .. key_{i-1}=B_{i-1} AND key_i &gt; B_i),
/// plus (all values equal) when inclusive. Upper mirrors with &lt;.
/// "&gt;"/"&lt;" are cross-type (rank-aware) and direction-aware (DESC inverts).
/// </summary>
internal static class CursorSql
{
    internal static string? Build(CursorRangeNode range, IReadOnlyList<SortKey> keys, ParameterBag bag, string alias) =>
        Build(range, keys, bag, new SchemaResolver(DocumentSchema.Plain, alias));

    internal static string? Build(CursorRangeNode range, IReadOnlyList<SortKey> keys, ParameterBag bag, SchemaResolver resolver)
    {
        var parts = new List<string>();
        if (range.Lower is not null)
            parts.Add(BuildBound(range.Lower, keys, bag, resolver, isLower: true));
        if (range.Upper is not null)
            parts.Add(BuildBound(range.Upper, keys, bag, resolver, isLower: false));

        return parts.Count switch
        {
            0 => null,
            1 => parts[0],
            _ => $"({parts[0]}) AND ({parts[1]})",
        };
    }

    private static string BuildBound(SortBoundary bound, IReadOnlyList<SortKey> keys, ParameterBag bag, SchemaResolver resolver, bool isLower)
    {
        var count = Math.Min(bound.Values.Count, keys.Count);
        var levels = new List<string>();

        for (var i = 0; i < count; i++)
        {
            var parts = new List<string>();
            for (var j = 0; j < i; j++)
                parts.Add(OperatorRegistry.EmitEq(keys[j].Field, bound.Values[j], bag, resolver));

            var strictOp = StrictOp(keys[i].Direction, isLower);
            parts.Add(OperatorRegistry.EmitCrossTypeComparison(keys[i].Field, strictOp, bound.Values[i], bag, resolver));

            levels.Add(parts.Count == 1 ? parts[0] : $"({string.Join(" AND ", parts)})");
        }

        if (bound.Inclusive)
        {
            var eqs = Enumerable.Range(0, count)
                .Select(j => OperatorRegistry.EmitEq(keys[j].Field, bound.Values[j], bag, resolver));
            levels.Add($"({string.Join(" AND ", eqs)})");
        }

        return $"({string.Join(" OR ", levels)})";
    }

    /// <summary>"after the boundary" in sort order: ASC lower → &gt;, DESC lower → &lt;; upper mirrors.</summary>
    private static FilterOperator StrictOp(SortDirection dir, bool isLower) => (dir, isLower) switch
    {
        (SortDirection.Asc, true) => FilterOperator.Gt,
        (SortDirection.Desc, true) => FilterOperator.Lt,
        (SortDirection.Asc, false) => FilterOperator.Lt,
        (SortDirection.Desc, false) => FilterOperator.Gt,
        _ => throw new NotSupportedException($"Unexpected sort direction: {dir}"),
    };
}
