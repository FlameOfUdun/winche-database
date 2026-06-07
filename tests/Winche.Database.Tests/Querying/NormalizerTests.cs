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
        var plan = Normalizer.Normalize(new QueryAst("users"));

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
        var plan = Normalizer.Normalize(new QueryAst("c",
            OrderBy: [new OrderAst(F("a")), new OrderAst(F("b"), SortDirection.Desc)]));

        var sort = plan.Nodes.OfType<SortNode>().Single();
        Assert.Equal(3, sort.Keys.Count);
        Assert.Equal(F("__name__"), sort.Keys[2].Field);
        Assert.Equal(SortDirection.Desc, sort.Keys[2].Direction);
    }

    [Fact]
    public void ExplicitNameSort_NoDuplicateTiebreak()
    {
        var plan = Normalizer.Normalize(new QueryAst("c", OrderBy: [new OrderAst(F("__name__"), SortDirection.Desc)]));
        var sort = plan.Nodes.OfType<SortNode>().Single();
        Assert.Single(sort.Keys);
    }

    [Fact]
    public void OrderByField_InjectsImplicitExistsFilter()
    {
        var plan = Normalizer.Normalize(new QueryAst("c", OrderBy: [new OrderAst(F("age"))]));

        var filter = plan.Nodes.OfType<FilterNode>().Single();
        var unary = Assert.IsType<UnaryFilterAst>(filter.Predicate);
        Assert.Equal(F("age"), unary.Field);
        Assert.Equal(UnaryOp.Exists, unary.Op);
    }

    [Fact]
    public void OrderByWithWhere_AndsExistsBeforeUserFilter()
    {
        var userFilter = new FieldFilterAst(F("x"), FilterOperator.Eq, new BooleanValue(true));
        var plan = Normalizer.Normalize(new QueryAst("c", Where: userFilter, OrderBy: [new OrderAst(F("age"))]));

        var and = Assert.IsType<CompositeFilterAst>(plan.Nodes.OfType<FilterNode>().Single().Predicate);
        Assert.Equal(CompositeOp.And, and.Op);
        Assert.Equal(UnaryOp.Exists, Assert.IsType<UnaryFilterAst>(and.Filters[0]).Op);
        Assert.Equal(userFilter, and.Filters[1]);
    }

    [Fact]
    public void EqNull_RewritesToIsNull()
    {
        var plan = Normalizer.Normalize(new QueryAst("c",
            Where: new FieldFilterAst(F("x"), FilterOperator.Eq, new NullValue())));
        var unary = Assert.IsType<UnaryFilterAst>(plan.Nodes.OfType<FilterNode>().Single().Predicate);
        Assert.Equal(UnaryOp.IsNull, unary.Op);
    }

    [Fact]
    public void NeNull_RewritesToExistsAndNotIsNull()
    {
        var plan = Normalizer.Normalize(new QueryAst("c",
            Where: new FieldFilterAst(F("x"), FilterOperator.Ne, new NullValue())));
        var and = Assert.IsType<CompositeFilterAst>(plan.Nodes.OfType<FilterNode>().Single().Predicate);
        Assert.Equal(3, and.Filters.Count);
        Assert.Equal(UnaryOp.Exists, Assert.IsType<UnaryFilterAst>(and.Filters[0]).Op);
        var notNull = Assert.IsType<CompositeFilterAst>(and.Filters[1]);
        Assert.Equal(CompositeOp.Not, notNull.Op);
        Assert.Equal(UnaryOp.IsNull, Assert.IsType<UnaryFilterAst>(notNull.Filters[0]).Op);
        var notNan = Assert.IsType<CompositeFilterAst>(and.Filters[2]);
        Assert.Equal(CompositeOp.Not, notNan.Op);
        Assert.Equal(UnaryOp.IsNan, Assert.IsType<UnaryFilterAst>(notNan.Filters[0]).Op);
    }

    [Fact]
    public void Cursors_MapToBoundariesWithInclusivity()
    {
        var plan = Normalizer.Normalize(new QueryAst("c",
            OrderBy: [new OrderAst(F("age"))],
            Start: new CursorAst([new IntegerValue(18)], Before: true),    // StartAt → inclusive lower
            End: new CursorAst([new IntegerValue(65)], Before: true)));    // EndBefore → exclusive upper

        var range = plan.Nodes.OfType<CursorRangeNode>().Single();
        Assert.True(range.Lower!.Inclusive);
        Assert.Equal([new IntegerValue(18)], range.Lower.Values);
        Assert.False(range.Upper!.Inclusive);

        var plan2 = Normalizer.Normalize(new QueryAst("c",
            OrderBy: [new OrderAst(F("age"))],
            Start: new CursorAst([new IntegerValue(18)], Before: false),   // StartAfter → exclusive
            End: new CursorAst([new IntegerValue(65)], Before: false)));   // EndAt → inclusive
        var range2 = plan2.Nodes.OfType<CursorRangeNode>().Single();
        Assert.False(range2.Lower!.Inclusive);
        Assert.True(range2.Upper!.Inclusive);
    }

    [Fact]
    public void NodeOrder_IsScanFilterSortCursorPage()
    {
        var plan = Normalizer.Normalize(new QueryAst("c",
            Where: new UnaryFilterAst(F("x"), UnaryOp.Exists),
            OrderBy: [new OrderAst(F("a"))],
            Start: new CursorAst([new IntegerValue(1)], Before: true)));

        Assert.IsType<CollectionScan>(plan.Nodes[0]);
        Assert.IsType<FilterNode>(plan.Nodes[1]);
        Assert.IsType<SortNode>(plan.Nodes[2]);
        Assert.IsType<CursorRangeNode>(plan.Nodes[3]);
        Assert.IsType<PageNode>(plan.Nodes[4]);
    }

    // ── Validation ───────────────────────────────────────────────────────────

    private static PlanValidationException Throws(QueryAst q) =>
        Assert.Throws<PlanValidationException>(() => Normalizer.Normalize(q));

    [Fact]
    public void EmptyCollection_Throws() =>
        Assert.Equal("EMPTY_COLLECTION", Throws(new QueryAst("")).Code);

    [Fact]
    public void DocumentPathAsCollection_Throws() =>
        Assert.Equal("BAD_COLLECTION_PATH", Throws(new QueryAst("users/u1")).Code);

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonPositiveLimit_Throws(int limit) =>
        Assert.Equal("BAD_LIMIT", Throws(new QueryAst("c", Limit: limit)).Code);

    [Fact]
    public void CursorWiderThanSortKeys_Throws()
    {
        // 1 orderBy + __name__ tiebreak = 2 sort keys; 3 cursor values is too many
        var q = new QueryAst("c", OrderBy: [new OrderAst(F("a"))],
            Start: new CursorAst([new IntegerValue(1), new IntegerValue(2), new IntegerValue(3)], Before: true));
        Assert.Equal("CURSOR_ARITY", Throws(q).Code);
    }

    [Fact]
    public void EmptyCursor_Throws()
    {
        var q = new QueryAst("c", OrderBy: [new OrderAst(F("a"))], Start: new CursorAst([], Before: true));
        Assert.Equal("CURSOR_ARITY", Throws(q).Code);
    }

    [Theory]
    [InlineData(FilterOperator.In)]
    [InlineData(FilterOperator.NotIn)]
    [InlineData(FilterOperator.ArrayContainsAny)]
    [InlineData(FilterOperator.ArrayContainsAll)]
    public void ArrayOperandOps_RejectNonArrayOperand(FilterOperator op) =>
        Assert.Equal("OPERAND_TYPE",
            Throws(new QueryAst("c", Where: new FieldFilterAst(F("f"), op, new IntegerValue(1)))).Code);

    [Theory]
    [InlineData(FilterOperator.In)]
    [InlineData(FilterOperator.NotIn)]
    public void InNotIn_RejectEmptyArray(FilterOperator op) =>
        Assert.Equal("OPERAND_TYPE",
            Throws(new QueryAst("c", Where: new FieldFilterAst(F("f"), op, new ArrayValue([])))).Code);

    [Theory]
    [InlineData(FilterOperator.Contains)]
    [InlineData(FilterOperator.StartsWith)]
    [InlineData(FilterOperator.EndsWith)]
    [InlineData(FilterOperator.Regex)]
    public void StringOps_RejectNonStringOperand(FilterOperator op) =>
        Assert.Equal("OPERAND_TYPE",
            Throws(new QueryAst("c", Where: new FieldFilterAst(F("f"), op, new IntegerValue(1)))).Code);

    [Fact]
    public void EmptyComposite_Throws() =>
        Assert.Equal("EMPTY_COMPOSITE",
            Throws(new QueryAst("c", Where: new CompositeFilterAst(CompositeOp.And, []))).Code);

    [Fact]
    public void NotWithTwoChildren_Throws() =>
        Assert.Equal("NOT_ARITY",
            Throws(new QueryAst("c", Where: new CompositeFilterAst(CompositeOp.Not,
                [new UnaryFilterAst(F("a"), UnaryOp.Exists), new UnaryFilterAst(F("b"), UnaryOp.Exists)]))).Code);

    [Theory]
    [InlineData(FilterOperator.In)]
    [InlineData(FilterOperator.ArrayContains)]
    [InlineData(FilterOperator.Contains)]
    public void Compare_RejectsNonComparisonOps(FilterOperator op) =>
        Assert.Equal("COMPARE_OP",
            Throws(new QueryAst("c", Where: new FieldCompareAst(F("a"), op, F("b")))).Code);

    [Fact]
    public void CursorValueOnNameKey_MustBeStringOrReference()
    {
        // default sort = __name__ only; integer cursor value is invalid
        var q = new QueryAst("c", Start: new CursorAst([new IntegerValue(5)], Before: true));
        Assert.Equal("CURSOR_TYPE", Throws(q).Code);

        // string and reference are both fine
        Normalizer.Normalize(new QueryAst("c", Start: new CursorAst([new StringValue("c/a")], Before: true)));
        Normalizer.Normalize(new QueryAst("c", Start: new CursorAst([new ReferenceValue("c/a")], Before: true)));
    }

    // ── I2: Typed error for unsupported operators on __name__ ────────────────

    [Fact]
    public void NameField_RejectsNonComparisonOperators()
    {
        Assert.Equal("NAME_OPERATOR", Throws(new QueryAst("c",
            Where: new FieldFilterAst(F("__name__"), FilterOperator.Contains, new StringValue("x")))).Code);
        Assert.Equal("NAME_OPERATOR", Throws(new QueryAst("c",
            Where: new UnaryFilterAst(F("__name__"), UnaryOp.Exists))).Code);
        // comparisons stay legal
        Normalizer.Normalize(new QueryAst("c",
            Where: new FieldFilterAst(F("__name__"), FilterOperator.Gt, new StringValue("c/a"))));
    }
}
