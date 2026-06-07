using Winche.Database.Core.Infrastructure;
using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Values;

namespace Winche.Database.Querying.Planning;

/// <summary>
/// QueryAst → LogicalPlan. This is where Firestore's query rules live:
/// __name__ tiebreaker, implicit Exists for orderBy fields, null-filter rewrites,
/// cursor inclusivity mapping, limit+1, and all validation.
/// </summary>
public static class Normalizer
{
    public const int DefaultLimit = 100;
    private static readonly FieldPath Name = FieldPath.Parse("__name__");

    public static LogicalPlan Normalize(QueryAst q)
    {
        if (string.IsNullOrEmpty(q.Collection))
            throw new PlanValidationException("EMPTY_COLLECTION", "'collection' must be non-empty.");
        if (!DocumentPathParser.IsValidCollectionPath(q.Collection, out var pathError))
            throw new PlanValidationException("BAD_COLLECTION_PATH", pathError!);

        var limit = q.Limit ?? DefaultLimit;
        if (limit < 1)
            throw new PlanValidationException("BAD_LIMIT", $"'limit' must be >= 1, got {limit}.");

        var sortKeys = BuildSortKeys(q.OrderBy);
        var predicate = BuildPredicate(q.Where, q.OrderBy);
        var range = BuildCursorRange(q.Start, q.End, sortKeys);

        var nodes = new List<PlanNode> { new CollectionScan(q.Collection) };
        if (predicate is not null) nodes.Add(new FilterNode(predicate));
        nodes.Add(new SortNode(sortKeys));
        if (range is not null) nodes.Add(range);
        nodes.Add(new PageNode(limit, Skip: 0, FetchExtraRow: true));

        return new LogicalPlan(nodes);
    }

    // ── Sort keys: append __name__ tiebreaker with the last key's direction ──

    private static List<SortKey> BuildSortKeys(IReadOnlyList<OrderAst>? orderBy)
    {
        var keys = (orderBy ?? []).Select(o => new SortKey(o.Field, o.Direction)).ToList();
        if (!keys.Any(k => k.Field.Equals(Name)))
            keys.Add(new SortKey(Name, keys.Count > 0 ? keys[^1].Direction : SortDirection.Asc));
        return keys;
    }

    // ── Predicate: implicit Exists for orderBy fields + validated/rewritten user filter ──

    private static FilterAst? BuildPredicate(FilterAst? where, IReadOnlyList<OrderAst>? orderBy)
    {
        var parts = new List<FilterAst>();

        foreach (var o in orderBy ?? [])
            if (!o.Field.Equals(Name))
                parts.Add(new UnaryFilterAst(o.Field, UnaryOp.Exists));

        if (where is not null)
            parts.Add(FilterRules.Prepare(where));

        return parts.Count switch
        {
            0 => null,
            1 => parts[0],
            _ => new CompositeFilterAst(CompositeOp.And, parts),
        };
    }

    // ── Cursors ───────────────────────────────────────────────────────────────

    private static CursorRangeNode? BuildCursorRange(CursorAst? start, CursorAst? end, IReadOnlyList<SortKey> sortKeys)
    {
        if (start is null && end is null) return null;

        return new CursorRangeNode(
            Lower: start is null ? null : new SortBoundary(ValidateCursor(start, sortKeys, "start"), Inclusive: start.Before),
            Upper: end is null ? null : new SortBoundary(ValidateCursor(end, sortKeys, "end"), Inclusive: !end.Before));
    }

    private static IReadOnlyList<Value> ValidateCursor(CursorAst cursor, IReadOnlyList<SortKey> sortKeys, string which)
    {
        if (cursor.Values.Count == 0 || cursor.Values.Count > sortKeys.Count)
            throw new PlanValidationException("CURSOR_ARITY",
                $"'{which}' must have between 1 and {sortKeys.Count} values (one per sort key), got {cursor.Values.Count}.");

        for (var i = 0; i < cursor.Values.Count; i++)
        {
            if (sortKeys[i].Field.Equals(Name) && cursor.Values[i] is not (StringValue or ReferenceValue))
                throw new PlanValidationException("CURSOR_TYPE",
                    $"'{which}' value {i} corresponds to __name__ and must be a string or reference.");
        }

        return cursor.Values;
    }
}

/// <summary>Filter validation + null-rewrites shared by Normalizer and PipelineNormalizer.</summary>
internal static class FilterRules
{
    private static bool IsNameField(FieldPath field) =>
        field.Segments.Count == 1 && field.Segments[0] == "__name__";

    /// <summary>Validate then apply null rewrites.</summary>
    internal static FilterAst Prepare(FilterAst f) => Rewrite(Validate(f));

    internal static FilterAst Validate(FilterAst f)
    {
        switch (f)
        {
            case CompositeFilterAst c:
                if (c.Filters.Count == 0)
                    throw new PlanValidationException("EMPTY_COMPOSITE", $"'{c.Op}' must have at least one child.");
                if (c.Op == CompositeOp.Not && c.Filters.Count != 1)
                    throw new PlanValidationException("NOT_ARITY", "'not' must have exactly one child.");
                foreach (var child in c.Filters) Validate(child);
                break;

            case FieldFilterAst ff:
                if (IsNameField(ff.Field))
                {
                    if (ff.Op is not (FilterOperator.Eq or FilterOperator.Ne or FilterOperator.Gt
                        or FilterOperator.Gte or FilterOperator.Lt or FilterOperator.Lte))
                        throw new PlanValidationException("NAME_OPERATOR",
                            "__name__ supports eq/ne/gt/gte/lt/lte only.");
                }
                else
                {
                    ValidateOperand(ff);
                }
                break;

            case UnaryFilterAst uf:
                if (IsNameField(uf.Field))
                    throw new PlanValidationException("NAME_OPERATOR",
                        "__name__ supports eq/ne/gt/gte/lt/lte only.");
                break;

            case FieldCompareAst cmp:
                if (cmp.Op is not (FilterOperator.Eq or FilterOperator.Ne or FilterOperator.Gt
                    or FilterOperator.Gte or FilterOperator.Lt or FilterOperator.Lte))
                    throw new PlanValidationException("COMPARE_OP",
                        $"'compare' supports eq/ne/gt/gte/lt/lte, got {cmp.Op}.");
                break;
        }
        return f;
    }

    internal static void ValidateOperand(FieldFilterAst f)
    {
        switch (f.Op)
        {
            case FilterOperator.In or FilterOperator.NotIn:
                if (f.Operand is not ArrayValue inArr || inArr.Values.Count == 0)
                    throw new PlanValidationException("OPERAND_TYPE", $"'{f.Op}' requires a non-empty array operand.");
                break;

            case FilterOperator.ArrayContainsAny or FilterOperator.ArrayContainsAll:
                if (f.Operand is not ArrayValue anyArr || anyArr.Values.Count == 0)
                    throw new PlanValidationException("OPERAND_TYPE", $"'{f.Op}' requires a non-empty array operand.");
                break;

            case FilterOperator.Contains or FilterOperator.StartsWith
                or FilterOperator.EndsWith or FilterOperator.Regex:
                if (f.Operand is not StringValue)
                    throw new PlanValidationException("OPERAND_TYPE", $"'{f.Op}' requires a string operand.");
                break;
        }
    }

    /// <summary>Eq null → IsNull; Ne null → Exists AND NOT IsNull. Recurses through composites.</summary>
    internal static FilterAst Rewrite(FilterAst f) => f switch
    {
        FieldFilterAst { Op: FilterOperator.Eq, Operand: NullValue } ff =>
            new UnaryFilterAst(ff.Field, UnaryOp.IsNull),

        FieldFilterAst { Op: FilterOperator.Ne, Operand: NullValue } ff =>
            new CompositeFilterAst(CompositeOp.And,
            [
                new UnaryFilterAst(ff.Field, UnaryOp.Exists),
                new CompositeFilterAst(CompositeOp.Not, [new UnaryFilterAst(ff.Field, UnaryOp.IsNull)]),
                new CompositeFilterAst(CompositeOp.Not, [new UnaryFilterAst(ff.Field, UnaryOp.IsNan)]),
            ]),

        CompositeFilterAst c => new CompositeFilterAst(c.Op, [.. c.Filters.Select(Rewrite)]),

        _ => f,
    };
}
