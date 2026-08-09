// tests/Winche.Database.Tests/Querying/NormalizerTests.cs
using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Planning;
using Winche.Database.Values;

namespace Winche.Database.Tests.Querying;

public class NormalizerTests
{
    private static FieldPath F(string p) => FieldPath.Parse(p);

    [Fact]
    public void Minimal_ProducesScanSortPage_WithNameTiebreakAndDefaults()
    {
        var plan = Normalizer.Normalize(new Query("users"));

        var scan = Assert.IsType<CollectionScan>(plan.Nodes[0]);
        Assert.Equal("users", scan.Collection);
        var sort = Assert.IsType<SortNode>(plan.Nodes[1]);
        var key = Assert.Single(sort.Keys);
        Assert.Equal(F("__name__"), key.Field);
        Assert.Equal(SortDirection.Asc, key.Direction);
        var page = Assert.IsType<PageNode>(plan.Nodes[2]);
        Assert.Equal(100, page.Limit);          // default
        Assert.Equal(0, page.Skip);
        Assert.True(page.FetchExtraRow);
        Assert.Equal(3, plan.Nodes.Count);      // no Filter, no CursorRange
    }

    [Fact]
    public void NameTiebreak_TakesDirectionOfLastSortKey()
    {
        var plan = Normalizer.Normalize(new Query("c",
            OrderBy: [new Ordering(F("a")), new Ordering(F("b"), SortDirection.Desc)]));

        var sort = plan.Nodes.OfType<SortNode>().Single();
        Assert.Equal(3, sort.Keys.Count);
        Assert.Equal(F("__name__"), sort.Keys[2].Field);
        Assert.Equal(SortDirection.Desc, sort.Keys[2].Direction);
    }

    [Fact]
    public void ExplicitNameSort_NoDuplicateTiebreak()
    {
        var plan = Normalizer.Normalize(new Query("c", OrderBy: [new Ordering(F("__name__"), SortDirection.Desc)]));
        var sort = plan.Nodes.OfType<SortNode>().Single();
        Assert.Single(sort.Keys);
    }

    [Fact]
    public void OrderByField_InjectsImplicitExistsFilter()
    {
        var plan = Normalizer.Normalize(new Query("c", OrderBy: [new Ordering(F("age"))]));

        var filter = plan.Nodes.OfType<FilterNode>().Single();
        var unary = Assert.IsType<UnaryFilter>(filter.Predicate);
        Assert.Equal(F("age"), unary.Field);
        Assert.Equal(UnaryOp.Exists, unary.Op);
    }

    [Fact]
    public void OrderByWithWhere_AndsExistsBeforeUserFilter()
    {
        var userFilter = new FieldFilter(F("x"), FilterOperator.Eq, new BooleanValue(true));
        var plan = Normalizer.Normalize(new Query("c", Where: userFilter, OrderBy: [new Ordering(F("age"))]));

        var and = Assert.IsType<CompositeFilter>(plan.Nodes.OfType<FilterNode>().Single().Predicate);
        Assert.Equal(CompositeOp.And, and.Op);
        Assert.Equal(UnaryOp.Exists, Assert.IsType<UnaryFilter>(and.Filters[0]).Op);
        Assert.Equal(userFilter, and.Filters[1]);
    }

    [Fact]
    public void EqNull_RewritesToIsNull()
    {
        var plan = Normalizer.Normalize(new Query("c",
            Where: new FieldFilter(F("x"), FilterOperator.Eq, new NullValue())));
        var unary = Assert.IsType<UnaryFilter>(plan.Nodes.OfType<FilterNode>().Single().Predicate);
        Assert.Equal(UnaryOp.IsNull, unary.Op);
    }

    [Fact]
    public void NeNull_RewritesToExistsAndNotIsNull()
    {
        var plan = Normalizer.Normalize(new Query("c",
            Where: new FieldFilter(F("x"), FilterOperator.Ne, new NullValue())));
        var and = Assert.IsType<CompositeFilter>(plan.Nodes.OfType<FilterNode>().Single().Predicate);
        Assert.Equal(3, and.Filters.Count);
        Assert.Equal(UnaryOp.Exists, Assert.IsType<UnaryFilter>(and.Filters[0]).Op);
        var notNull = Assert.IsType<CompositeFilter>(and.Filters[1]);
        Assert.Equal(CompositeOp.Not, notNull.Op);
        Assert.Equal(UnaryOp.IsNull, Assert.IsType<UnaryFilter>(notNull.Filters[0]).Op);
        var notNan = Assert.IsType<CompositeFilter>(and.Filters[2]);
        Assert.Equal(CompositeOp.Not, notNan.Op);
        Assert.Equal(UnaryOp.IsNan, Assert.IsType<UnaryFilter>(notNan.Filters[0]).Op);
    }

    [Fact]
    public void Cursors_MapToBoundariesWithInclusivity()
    {
        var plan = Normalizer.Normalize(new Query("c",
            OrderBy: [new Ordering(F("age"))],
            Start: new Cursor([new IntegerValue(18)], Before: true),    // StartAt → inclusive lower
            End: new Cursor([new IntegerValue(65)], Before: true)));    // EndBefore → exclusive upper

        var range = plan.Nodes.OfType<CursorRangeNode>().Single();
        Assert.True(range.Lower!.Inclusive);
        Assert.Equal([new IntegerValue(18)], range.Lower.Values);
        Assert.False(range.Upper!.Inclusive);

        var plan2 = Normalizer.Normalize(new Query("c",
            OrderBy: [new Ordering(F("age"))],
            Start: new Cursor([new IntegerValue(18)], Before: false),   // StartAfter → exclusive
            End: new Cursor([new IntegerValue(65)], Before: false)));   // EndAt → inclusive
        var range2 = plan2.Nodes.OfType<CursorRangeNode>().Single();
        Assert.False(range2.Lower!.Inclusive);
        Assert.True(range2.Upper!.Inclusive);
    }

    [Fact]
    public void NodeOrder_IsScanFilterSortCursorPage()
    {
        var plan = Normalizer.Normalize(new Query("c",
            Where: new UnaryFilter(F("x"), UnaryOp.Exists),
            OrderBy: [new Ordering(F("a"))],
            Start: new Cursor([new IntegerValue(1)], Before: true)));

        Assert.IsType<CollectionScan>(plan.Nodes[0]);
        Assert.IsType<FilterNode>(plan.Nodes[1]);
        Assert.IsType<SortNode>(plan.Nodes[2]);
        Assert.IsType<CursorRangeNode>(plan.Nodes[3]);
        Assert.IsType<PageNode>(plan.Nodes[4]);
    }

    // ── Validation ───────────────────────────────────────────────────────────

    private static PlanValidationException Throws(Query q) =>
        Assert.Throws<PlanValidationException>(() => Normalizer.Normalize(q));

    [Fact]
    public void EmptyCollection_Throws() =>
        Assert.Equal("EMPTY_COLLECTION", Throws(new Query("")).Code);

    [Fact]
    public void DocumentPathAsCollection_Throws() =>
        Assert.Equal("BAD_COLLECTION_PATH", Throws(new Query("users/u1")).Code);

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonPositiveLimit_Throws(int limit) =>
        Assert.Equal("BAD_LIMIT", Throws(new Query("c", Limit: limit)).Code);

    [Fact]
    public void CursorWiderThanSortKeys_Throws()
    {
        // 1 orderBy + __name__ tiebreak = 2 sort keys; 3 cursor values is too many
        var q = new Query("c", OrderBy: [new Ordering(F("a"))],
            Start: new Cursor([new IntegerValue(1), new IntegerValue(2), new IntegerValue(3)], Before: true));
        Assert.Equal("CURSOR_ARITY", Throws(q).Code);
    }

    [Fact]
    public void EmptyCursor_Throws()
    {
        var q = new Query("c", OrderBy: [new Ordering(F("a"))], Start: new Cursor([], Before: true));
        Assert.Equal("CURSOR_ARITY", Throws(q).Code);
    }

    [Theory]
    [InlineData(FilterOperator.In)]
    [InlineData(FilterOperator.NotIn)]
    [InlineData(FilterOperator.ArrayContainsAny)]
    [InlineData(FilterOperator.ArrayContainsAll)]
    public void ArrayOperandOps_RejectNonArrayOperand(FilterOperator op) =>
        Assert.Equal("OPERAND_TYPE",
            Throws(new Query("c", Where: new FieldFilter(F("f"), op, new IntegerValue(1)))).Code);

    [Theory]
    [InlineData(FilterOperator.In)]
    [InlineData(FilterOperator.NotIn)]
    public void InNotIn_RejectEmptyArray(FilterOperator op) =>
        Assert.Equal("OPERAND_TYPE",
            Throws(new Query("c", Where: new FieldFilter(F("f"), op, new ArrayValue([])))).Code);

    [Theory]
    [InlineData(FilterOperator.Contains)]
    [InlineData(FilterOperator.StartsWith)]
    [InlineData(FilterOperator.EndsWith)]
    [InlineData(FilterOperator.Regex)]
    public void StringOps_RejectNonStringOperand(FilterOperator op) =>
        Assert.Equal("OPERAND_TYPE",
            Throws(new Query("c", Where: new FieldFilter(F("f"), op, new IntegerValue(1)))).Code);

    [Fact]
    public void Regex_AtMaxLength_IsAccepted()
    {
        var pattern = new string('a', FilterRules.MaxRegexPatternLength);
        var plan = Normalizer.Normalize(new Query("c", Where: new FieldFilter(F("f"), FilterOperator.Regex, new StringValue(pattern))));
        Assert.NotNull(plan);
    }

    [Fact]
    public void Regex_OverMaxLength_Throws() =>
        Assert.Equal("REGEX_TOO_LONG",
            Throws(new Query("c", Where: new FieldFilter(
                F("f"), FilterOperator.Regex, new StringValue(new string('a', FilterRules.MaxRegexPatternLength + 1))))).Code);

    [Fact]
    public void EmptyComposite_Throws() =>
        Assert.Equal("EMPTY_COMPOSITE",
            Throws(new Query("c", Where: new CompositeFilter(CompositeOp.And, []))).Code);

    [Fact]
    public void NotWithTwoChildren_Throws() =>
        Assert.Equal("NOT_ARITY",
            Throws(new Query("c", Where: new CompositeFilter(CompositeOp.Not,
                [new UnaryFilter(F("a"), UnaryOp.Exists), new UnaryFilter(F("b"), UnaryOp.Exists)]))).Code);

    [Theory]
    [InlineData(FilterOperator.In)]
    [InlineData(FilterOperator.ArrayContains)]
    [InlineData(FilterOperator.Contains)]
    public void Compare_RejectsNonComparisonOps(FilterOperator op) =>
        Assert.Equal("COMPARE_OP",
            Throws(new Query("c", Where: new FieldCompare(F("a"), op, F("b")))).Code);

    [Fact]
    public void CursorValueOnNameKey_MustBeStringOrReference()
    {
        // default sort = __name__ only; integer cursor value is invalid
        var q = new Query("c", Start: new Cursor([new IntegerValue(5)], Before: true));
        Assert.Equal("CURSOR_TYPE", Throws(q).Code);

        // string and reference are both fine
        Normalizer.Normalize(new Query("c", Start: new Cursor([new StringValue("c/a")], Before: true)));
        Normalizer.Normalize(new Query("c", Start: new Cursor([new ReferenceValue("c/a")], Before: true)));
    }

    // ── I2: Typed error for unsupported operators on __name__ ────────────────

    [Fact]
    public void NameField_RejectsNonComparisonOperators()
    {
        Assert.Equal("NAME_OPERATOR", Throws(new Query("c",
            Where: new FieldFilter(F("__name__"), FilterOperator.Contains, new StringValue("x")))).Code);
        Assert.Equal("NAME_OPERATOR", Throws(new Query("c",
            Where: new UnaryFilter(F("__name__"), UnaryOp.Exists))).Code);
        // comparisons stay legal
        Normalizer.Normalize(new Query("c",
            Where: new FieldFilter(F("__name__"), FilterOperator.Gt, new StringValue("c/a"))));
    }

    // ── QueryLimits: configurable ceilings on request shape ───────────────────

    private static PlanValidationException ThrowsWithLimits(Query q, QueryLimits limits) =>
        Assert.Throws<PlanValidationException>(() => Normalizer.Normalize(q, limits));

    [Fact]
    public void NoLimitsSupplied_CeilingsAreNotEnforced()
    {
        // Omitting QueryLimits skips the new ceiling checks entirely (pre-existing floor/shape
        // checks still apply) — enforcement is the transport's job via QueryShapeValidator.
        var hugeOrderBy = Enumerable.Range(0, 5000).Select(i => new Ordering(F($"f{i}"))).ToList();
        Normalizer.Normalize(new Query("c", OrderBy: hugeOrderBy, Limit: int.MaxValue, Offset: int.MaxValue));
    }

    [Fact]
    public void Limit_AtCeiling_Succeeds_OverCeiling_Throws()
    {
        var limits = new QueryLimits { MaxLimit = 10 };
        Normalizer.Normalize(new Query("c", Limit: 10), limits);                 // at the limit
        Assert.Equal("LIMIT_TOO_LARGE", ThrowsWithLimits(new Query("c", Limit: 11), limits).Code);
    }

    [Fact]
    public void LimitToLast_AtCeiling_Succeeds_OverCeiling_Throws()
    {
        var limits = new QueryLimits { MaxLimit = 10 };
        var ob = new List<Ordering> { new(F("a")) };
        Normalizer.Normalize(new Query("c", OrderBy: ob, LimitToLast: 10), limits);
        Assert.Equal("LIMIT_TO_LAST_TOO_LARGE",
            ThrowsWithLimits(new Query("c", OrderBy: ob, LimitToLast: 11), limits).Code);
    }

    [Fact]
    public void Offset_AtCeiling_Succeeds_OverCeiling_Throws()
    {
        var limits = new QueryLimits { MaxOffset = 10 };
        Normalizer.Normalize(new Query("c", Offset: 10), limits);
        Assert.Equal("OFFSET_TOO_LARGE", ThrowsWithLimits(new Query("c", Offset: 11), limits).Code);
    }

    [Fact]
    public void OrderByCount_AtCeiling_Succeeds_OverCeiling_Throws()
    {
        var limits = new QueryLimits { MaxOrderByKeys = 5 };
        var atCeiling = Enumerable.Range(0, 5).Select(i => new Ordering(F($"f{i}"))).ToList();
        Normalizer.Normalize(new Query("c", OrderBy: atCeiling), limits);

        var overCeiling = Enumerable.Range(0, 6).Select(i => new Ordering(F($"f{i}"))).ToList();
        Assert.Equal("TOO_MANY_ORDER_BY_KEYS",
            ThrowsWithLimits(new Query("c", OrderBy: overCeiling), limits).Code);
    }

    [Fact]
    public void OrderByCount_FarOverCeiling_RejectsImmediately_WithoutQuadraticCursorWork()
    {
        // Regression guard for the O(n^2) CursorSql.BuildBound blowup: a pathological orderBy +
        // full cursor must be rejected by the (cheap, linear) count check before the cursor is
        // ever built, so this must return well under a second even for a huge key count.
        var limits = new QueryLimits { MaxOrderByKeys = 32 };
        const int hugeCount = 20_000;
        var orderBy = Enumerable.Range(0, hugeCount).Select(i => new Ordering(F($"f{i}"))).ToList();
        var cursorValues = Enumerable.Range(0, hugeCount).Select(i => (Value)new IntegerValue(i)).ToList();
        var q = new Query("c", OrderBy: orderBy, Start: new Cursor(cursorValues, Before: true));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal("TOO_MANY_ORDER_BY_KEYS", ThrowsWithLimits(q, limits).Code);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"rejection took {sw.ElapsedMilliseconds}ms — expected near-instant");
    }

    [Fact]
    public void FieldPathSegmentCount_AtCeiling_Succeeds_OverCeiling_Throws()
    {
        var limits = new QueryLimits { MaxFieldPathSegments = 3 };
        Normalizer.Normalize(new Query("c", OrderBy: [new Ordering(F("a.b.c"))]), limits);
        Assert.Equal("FIELD_PATH_TOO_DEEP",
            ThrowsWithLimits(new Query("c", OrderBy: [new Ordering(F("a.b.c.d"))]), limits).Code);
    }

    [Fact]
    public void FieldPathSegmentLength_AtCeiling_Succeeds_OverCeiling_Throws()
    {
        var limits = new QueryLimits { MaxFieldPathSegmentLength = 10 };
        Normalizer.Normalize(new Query("c", OrderBy: [new Ordering(F(new string('a', 10)))]), limits);
        Assert.Equal("FIELD_PATH_SEGMENT_TOO_LONG",
            ThrowsWithLimits(new Query("c", OrderBy: [new Ordering(F(new string('a', 11)))]), limits).Code);
    }

    [Fact]
    public void SelectFieldPath_IsAlsoBoundedByFieldPathLimits()
    {
        var limits = new QueryLimits { MaxFieldPathSegments = 2 };
        Normalizer.Normalize(new Query("c", Select: [F("a.b")]), limits);
        Assert.Equal("FIELD_PATH_TOO_DEEP",
            ThrowsWithLimits(new Query("c", Select: [F("a.b.c")]), limits).Code);
    }

    [Theory]
    [InlineData(FilterOperator.In)]
    [InlineData(FilterOperator.NotIn)]
    [InlineData(FilterOperator.ArrayContainsAny)]
    [InlineData(FilterOperator.ArrayContainsAll)]
    public void ArrayOperandCount_AtCeiling_Succeeds_OverCeiling_Throws(FilterOperator op)
    {
        var limits = new QueryLimits { MaxArrayOperandCount = 3 };
        Value[] AtCeiling() => [new IntegerValue(1), new IntegerValue(2), new IntegerValue(3)];
        Value[] OverCeiling() => [new IntegerValue(1), new IntegerValue(2), new IntegerValue(3), new IntegerValue(4)];

        Normalizer.Normalize(new Query("c", Where: new FieldFilter(F("f"), op, new ArrayValue(AtCeiling()))), limits);
        Assert.Equal("TOO_MANY_OPERANDS",
            ThrowsWithLimits(new Query("c", Where: new FieldFilter(F("f"), op, new ArrayValue(OverCeiling()))), limits).Code);
    }
}
