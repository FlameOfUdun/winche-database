using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Values;

namespace Winche.Database.Querying.Planning;

/// <summary>
/// Query → LogicalPlan. This is where the document-model query rules live:
/// __name__ tiebreaker, implicit Exists for orderBy fields, null-filter rewrites,
/// cursor inclusivity mapping, limit+1, and all validation.
/// </summary>
internal static class Normalizer
{
    public const int DefaultLimit = 100;
    private static readonly FieldPath Name = FieldPath.Parse("__name__");

    // No ceiling applied when the caller omits QueryLimits — the pre-existing floor/shape checks
    // below are unaffected either way. Transports enforce the configured QueryLimits explicitly
    // (see QueryShapeValidator) before a request reaches this internal planning path.
    private static readonly QueryLimits Unbounded = new()
    {
        MaxLimit = int.MaxValue,
        MaxOffset = int.MaxValue,
        MaxOrderByKeys = int.MaxValue,
        MaxFieldPathSegments = int.MaxValue,
        MaxFieldPathSegmentLength = int.MaxValue,
        MaxArrayOperandCount = int.MaxValue,
    };

    /// <summary>
    /// Normalizes <paramref name="q"/> into a <see cref="LogicalPlan"/>, applying both the
    /// document-model query rules and the shape ceilings in <paramref name="limits"/> (omit to
    /// skip the ceiling checks entirely — see <see cref="Unbounded"/>).
    /// </summary>
    public static LogicalPlan Normalize(Query q, QueryLimits? limits = null)
    {
        var lim = limits ?? Unbounded;

        if (string.IsNullOrEmpty(q.Collection))
            throw new PlanValidationException("EMPTY_COLLECTION", "'collection' must be non-empty.");
        if (!DocumentPathParser.IsValidCollectionPath(q.Collection, out var pathError))
            throw new PlanValidationException("BAD_COLLECTION_PATH", pathError!);

        var (limit, skip, reverse) = ResolvePaging(q, lim);

        var sortKeys = BuildSortKeys(q.OrderBy, lim);
        if (reverse)
            sortKeys = sortKeys.Select(k => new SortKey(k.Field, Flip(k.Direction))).ToList();
        var predicate = BuildPredicate(q.Where, q.OrderBy, lim);
        var range = BuildCursorRange(q.Start, q.End, sortKeys);

        foreach (var s in q.Select ?? [])
            ValidateFieldPath(s, lim);

        var nodes = new List<PlanNode> { new CollectionScan(q.Collection) };
        if (predicate is not null) nodes.Add(new FilterNode(predicate));
        nodes.Add(new SortNode(sortKeys));
        if (range is not null) nodes.Add(range);
        nodes.Add(new PageNode(limit, Skip: skip, FetchExtraRow: true, ReverseResult: reverse));

        return new LogicalPlan(nodes);
    }

    // ── Paging resolution (limit / limitToLast / offset) ──────────────────────

    private static (int Limit, int Skip, bool Reverse) ResolvePaging(Query q, QueryLimits lim)
    {
        if (q.LimitToLast is { } ltl)
        {
            if (q.Limit is not null)
                throw new PlanValidationException("LIMIT_CONFLICT", "'limit' and 'limitToLast' are mutually exclusive.");
            if (q.Offset is not null)
                throw new PlanValidationException("OFFSET_LIMIT_TO_LAST", "'offset' cannot be combined with 'limitToLast'.");
            if (q.OrderBy is null || q.OrderBy.Count == 0)
                throw new PlanValidationException("LIMIT_TO_LAST_NO_ORDER", "'limitToLast' requires at least one 'orderBy'.");
            if (ltl < 1)
                throw new PlanValidationException("BAD_LIMIT_TO_LAST", $"'limitToLast' must be >= 1, got {ltl}.");
            if (ltl > lim.MaxLimit)
                throw new PlanValidationException("LIMIT_TO_LAST_TOO_LARGE", $"'limitToLast' must be <= {lim.MaxLimit}, got {ltl}.");
            return (ltl, 0, true);
        }

        var limit = q.Limit ?? DefaultLimit;
        if (limit < 1)
            throw new PlanValidationException("BAD_LIMIT", $"'limit' must be >= 1, got {limit}.");
        if (limit > lim.MaxLimit)
            throw new PlanValidationException("LIMIT_TOO_LARGE", $"'limit' must be <= {lim.MaxLimit}, got {limit}.");

        var offset = q.Offset ?? 0;
        if (offset < 0)
            throw new PlanValidationException("BAD_OFFSET", $"'offset' must be >= 0, got {offset}.");
        if (offset > lim.MaxOffset)
            throw new PlanValidationException("OFFSET_TOO_LARGE", $"'offset' must be <= {lim.MaxOffset}, got {offset}.");

        return (limit, offset, false);
    }

    private static SortDirection Flip(SortDirection d) =>
        d == SortDirection.Asc ? SortDirection.Desc : SortDirection.Asc;

    // ── Sort keys: append __name__ tiebreaker with the last key's direction ──

    private static List<SortKey> BuildSortKeys(IReadOnlyList<Ordering>? orderBy, QueryLimits lim)
    {
        if ((orderBy?.Count ?? 0) > lim.MaxOrderByKeys)
            throw new PlanValidationException("TOO_MANY_ORDER_BY_KEYS",
                $"'orderBy' may contain at most {lim.MaxOrderByKeys} keys, got {orderBy!.Count}.");
        foreach (var o in orderBy ?? [])
            ValidateFieldPath(o.Field, lim);

        var keys = (orderBy ?? []).Select(o => new SortKey(o.Field, o.Direction)).ToList();
        if (!keys.Any(k => k.Field.Equals(Name)))
            keys.Add(new SortKey(Name, keys.Count > 0 ? keys[^1].Direction : SortDirection.Asc));
        return keys;
    }

    // ── Field paths: bound segment count/length so a pathological path can't blow up generated SQL ──

    internal static void ValidateFieldPath(FieldPath field, QueryLimits lim)
    {
        if (field.Segments.Count > lim.MaxFieldPathSegments)
            throw new PlanValidationException("FIELD_PATH_TOO_DEEP",
                $"Field path '{field}' has {field.Segments.Count} segments, exceeding the {lim.MaxFieldPathSegments}-segment limit.");
        foreach (var segment in field.Segments)
            if (segment.Length > lim.MaxFieldPathSegmentLength)
                throw new PlanValidationException("FIELD_PATH_SEGMENT_TOO_LONG",
                    $"Field path '{field}' has a segment of {segment.Length} characters, exceeding the {lim.MaxFieldPathSegmentLength}-character limit.");
    }

    // ── Predicate: implicit Exists for orderBy fields + validated/rewritten user filter ──

    private static Filter? BuildPredicate(Filter? where, IReadOnlyList<Ordering>? orderBy, QueryLimits lim)
    {
        var parts = new List<Filter>();

        foreach (var o in orderBy ?? [])
            if (!o.Field.Equals(Name))
                parts.Add(new UnaryFilter(o.Field, UnaryOp.Exists));

        if (where is not null)
            parts.Add(FilterRules.Prepare(where, lim));

        return parts.Count switch
        {
            0 => null,
            1 => parts[0],
            _ => new CompositeFilter(CompositeOp.And, parts),
        };
    }

    // ── Cursors ───────────────────────────────────────────────────────────────

    private static CursorRangeNode? BuildCursorRange(Cursor? start, Cursor? end, IReadOnlyList<SortKey> sortKeys)
    {
        if (start is null && end is null) return null;

        return new CursorRangeNode(
            Lower: start is null ? null : new SortBoundary(ValidateCursor(start, sortKeys, "start"), Inclusive: start.Before),
            Upper: end is null ? null : new SortBoundary(ValidateCursor(end, sortKeys, "end"), Inclusive: !end.Before));
    }

    private static IReadOnlyList<Value> ValidateCursor(Cursor cursor, IReadOnlyList<SortKey> sortKeys, string which)
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

/// <summary>Filter validation + null-rewrites used by Normalizer.</summary>
internal static class FilterRules
{
    private static bool IsNameField(FieldPath field) =>
        field.Segments.Count == 1 && field.Segments[0] == "__name__";

    /// <summary>Validate then apply null rewrites.</summary>
    internal static Filter Prepare(Filter f, QueryLimits lim) => Rewrite(Validate(f, lim));

    internal static Filter Validate(Filter f, QueryLimits lim)
    {
        switch (f)
        {
            case CompositeFilter c:
                if (c.Filters.Count == 0)
                    throw new PlanValidationException("EMPTY_COMPOSITE", $"'{c.Op}' must have at least one child.");
                if (c.Op == CompositeOp.Not && c.Filters.Count != 1)
                    throw new PlanValidationException("NOT_ARITY", "'not' must have exactly one child.");
                foreach (var child in c.Filters) Validate(child, lim);
                break;

            case FieldFilter ff:
                Normalizer.ValidateFieldPath(ff.Field, lim);
                if (IsNameField(ff.Field))
                {
                    if (ff.Op is not (FilterOperator.Eq or FilterOperator.Ne or FilterOperator.Gt
                        or FilterOperator.Gte or FilterOperator.Lt or FilterOperator.Lte))
                        throw new PlanValidationException("NAME_OPERATOR",
                            "__name__ supports eq/ne/gt/gte/lt/lte only.");
                }
                else
                {
                    ValidateOperand(ff, lim);
                }
                break;

            case UnaryFilter uf:
                Normalizer.ValidateFieldPath(uf.Field, lim);
                if (IsNameField(uf.Field))
                    throw new PlanValidationException("NAME_OPERATOR",
                        "__name__ supports eq/ne/gt/gte/lt/lte only.");
                break;

            case FieldCompare cmp:
                Normalizer.ValidateFieldPath(cmp.Left, lim);
                Normalizer.ValidateFieldPath(cmp.Right, lim);
                if (cmp.Op is not (FilterOperator.Eq or FilterOperator.Ne or FilterOperator.Gt
                    or FilterOperator.Gte or FilterOperator.Lt or FilterOperator.Lte))
                    throw new PlanValidationException("COMPARE_OP",
                        $"'compare' supports eq/ne/gt/gte/lt/lte, got {cmp.Op}.");
                break;
        }
        return f;
    }

    /// <summary>
    /// A pattern this long is already far outside any legitimate use; rejecting it early is cheap
    /// defense in depth only — a short pattern can be just as expensive to match as a long one, so
    /// this cap does not by itself bound how long a 'regex' filter takes to evaluate.
    /// </summary>
    internal const int MaxRegexPatternLength = 512;

    internal static void ValidateOperand(FieldFilter f, QueryLimits lim)
    {
        switch (f.Op)
        {
            case FilterOperator.In or FilterOperator.NotIn:
                if (f.Operand is not ArrayValue inArr || inArr.Values.Count == 0)
                    throw new PlanValidationException("OPERAND_TYPE", $"'{f.Op}' requires a non-empty array operand.");
                if (inArr.Values.Count > lim.MaxArrayOperandCount)
                    throw new PlanValidationException("TOO_MANY_OPERANDS",
                        $"'{f.Op}' operand may contain at most {lim.MaxArrayOperandCount} values, got {inArr.Values.Count}.");
                break;

            case FilterOperator.ArrayContainsAny or FilterOperator.ArrayContainsAll:
                if (f.Operand is not ArrayValue anyArr || anyArr.Values.Count == 0)
                    throw new PlanValidationException("OPERAND_TYPE", $"'{f.Op}' requires a non-empty array operand.");
                if (anyArr.Values.Count > lim.MaxArrayOperandCount)
                    throw new PlanValidationException("TOO_MANY_OPERANDS",
                        $"'{f.Op}' operand may contain at most {lim.MaxArrayOperandCount} values, got {anyArr.Values.Count}.");
                break;

            case FilterOperator.Contains or FilterOperator.StartsWith
                or FilterOperator.EndsWith or FilterOperator.Regex:
                if (f.Operand is not StringValue sv)
                    throw new PlanValidationException("OPERAND_TYPE", $"'{f.Op}' requires a string operand.");
                if (f.Op == FilterOperator.Regex && sv.Value.Length > MaxRegexPatternLength)
                    throw new PlanValidationException("REGEX_TOO_LONG",
                        $"'regex' pattern may be at most {MaxRegexPatternLength} characters, got {sv.Value.Length}.");
                break;
        }
    }

    /// <summary>Eq null → IsNull; Ne null → Exists AND NOT IsNull. Recurses through composites.</summary>
    internal static Filter Rewrite(Filter f) => f switch
    {
        FieldFilter { Op: FilterOperator.Eq, Operand: NullValue } ff =>
            new UnaryFilter(ff.Field, UnaryOp.IsNull),

        FieldFilter { Op: FilterOperator.Ne, Operand: NullValue } ff =>
            new CompositeFilter(CompositeOp.And,
            [
                new UnaryFilter(ff.Field, UnaryOp.Exists),
                new CompositeFilter(CompositeOp.Not, [new UnaryFilter(ff.Field, UnaryOp.IsNull)]),
                new CompositeFilter(CompositeOp.Not, [new UnaryFilter(ff.Field, UnaryOp.IsNan)]),
            ]),

        CompositeFilter c => new CompositeFilter(c.Op, [.. c.Filters.Select(Rewrite)]),

        _ => f,
    };
}
