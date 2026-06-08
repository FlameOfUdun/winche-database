// tests/Winche.Database.Tests/Querying/PipelineNormalizerTests.cs
using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Planning;
using Winche.Database.Values;
// The group/project leaf types exist in both Ast and Planning; these tests build AST inputs, so
// qualify those constructors with Ast. (Plan node assertions use the unambiguous *Node types.)
using Ast = Winche.Database.Querying.Ast;

namespace Winche.Database.Tests.Querying;

public class PipelineNormalizerTests
{
    private static FieldPath F(string p) => FieldPath.Parse(p);
    private static Match Match(string c = "orders") => new(c, null);

    private static PlanValidationException Throws(params Stage[] stages) =>
        Assert.Throws<PlanValidationException>(() => PipelineNormalizer.Normalize(new Pipeline(stages)));

    [Fact]
    public void Match_BecomesScanPlusFilter()
    {
        var where = new FieldFilter(F("s"), FilterOperator.Eq, new BooleanValue(true));
        var plan = PipelineNormalizer.Normalize(new Pipeline([new Match("orders", where)]));

        Assert.Equal("orders", Assert.IsType<CollectionScan>(plan.Nodes[0]).Collection);
        Assert.Equal(where, Assert.IsType<FilterNode>(plan.Nodes[1]).Predicate);
        Assert.Equal(2, plan.Nodes.Count);                       // no Sort/Page added — pipelines are explicit
    }

    [Fact]
    public void Match_EqNullRewriteApplies()
    {
        var plan = PipelineNormalizer.Normalize(new Pipeline(
            [new Match("o", new FieldFilter(F("x"), FilterOperator.Eq, new NullValue()))]));
        var unary = Assert.IsType<UnaryFilter>(Assert.IsType<FilterNode>(plan.Nodes[1]).Predicate);
        Assert.Equal(UnaryOp.IsNull, unary.Op);
    }

    [Fact]
    public void Having_BecomesFilterAfterGroup()
    {
        var plan = PipelineNormalizer.Normalize(new Pipeline(
        [
            Match(),
            new Group(
                [new Ast.GroupKey("city", F("addr.city"))],
                [new Ast.Accumulator("n", AggFunction.Count)],
                Having: new FieldFilter(F("n"), FilterOperator.Gt, new IntegerValue(1))),
        ]));

        Assert.IsType<CollectionScan>(plan.Nodes[0]);
        Assert.IsType<GroupNode>(plan.Nodes[1]);
        Assert.IsType<FilterNode>(plan.Nodes[2]);
    }

    [Fact]
    public void AllStages_MapToNodes()
    {
        var plan = PipelineNormalizer.Normalize(new Pipeline(
        [
            Match(),
            new Where(new UnaryFilter(F("x"), UnaryOp.Exists)),
            new Lookup("users", F("uid"), F("__name__"), "user"),
            new Unwind(F("items"), "item"),
            new Group([], [new Ast.Accumulator("n", AggFunction.Count)]),
            new Project([new Ast.Projection("n", new Ast.FieldRefExpr(F("n")))]),
            new Sort([new Ordering(F("n"), SortDirection.Desc)]),
            new Skip(2),
            new Limit(7),
        ]));

        Assert.Collection(plan.Nodes,
            n => Assert.IsType<CollectionScan>(n),
            n => Assert.IsType<FilterNode>(n),
            n => Assert.IsType<LookupNode>(n),
            n => Assert.IsType<UnwindNode>(n),
            n => Assert.IsType<GroupNode>(n),
            n => Assert.IsType<ProjectNode>(n),
            n => Assert.IsType<SortNode>(n),
            n => Assert.IsType<SkipNode>(n),
            n => Assert.IsType<LimitNode>(n));
    }

    // ── Validation ───────────────────────────────────────────────────────────

    [Fact]
    public void EmptyPipeline_Throws() =>
        Assert.Equal("PIPELINE_EMPTY", Throws().Code);

    [Fact]
    public void FirstStageNotMatch_Throws() =>
        Assert.Equal("MATCH_FIRST", Throws(new Limit(1)).Code);

    [Fact]
    public void MatchNotFirstOnly_Throws() =>
        Assert.Equal("MATCH_FIRST", Throws(Match(), Match()).Code);

    [Fact]
    public void BadCollectionPath_Throws() =>
        Assert.Equal("BAD_COLLECTION_PATH", Throws(new Match("users/u1", null)).Code);

    [Theory]
    [InlineData("1bad")]
    [InlineData("has space")]
    [InlineData("quo\"te")]
    [InlineData("")]
    public void BadAsName_Throws(string name) =>
        Assert.Equal("AS_NAME", Throws(Match(), new Unwind(F("x"), name)).Code);

    [Fact]
    public void DuplicateAs_InGroup_Throws() =>
        Assert.Equal("DUPLICATE_AS", Throws(Match(), new Group(
            [new Ast.GroupKey("n", F("a"))],
            [new Ast.Accumulator("n", AggFunction.Count)])).Code);

    [Fact]
    public void AccumulatorNeedsField_ExceptCount() =>
        Assert.Equal("ACC_FIELD", Throws(Match(), new Group(
            [], [new Ast.Accumulator("s", AggFunction.Sum)])).Code);

    [Fact]
    public void ProjectAgg_OnlyCountSumAvg()
    {
        Assert.Equal("PROJECT_AGG", Throws(Match(), new Project(
            [new Ast.Projection("m", new Ast.AggFuncExpr(AggFunction.Min, F("x")))])).Code);
        // count/sum/avg fine:
        PipelineNormalizer.Normalize(new Pipeline([Match(), new Project(
            [new Ast.Projection("c", new Ast.AggFuncExpr(AggFunction.Count))])]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveLimit_Throws(int n) =>
        Assert.Equal("BAD_LIMIT", Throws(Match(), new Limit(n)).Code);

    [Fact]
    public void NegativeSkip_Throws() =>
        Assert.Equal("BAD_SKIP", Throws(Match(), new Skip(-1)).Code);

    [Fact]
    public void LookupBadLimit_Throws() =>
        Assert.Equal("BAD_LIMIT", Throws(Match(),
            new Lookup("u", F("a"), F("b"), "x", Limit: 0)).Code);

    [Fact]
    public void FilterOperandValidation_AppliesInsidePipeline() =>
        Assert.Equal("OPERAND_TYPE", Throws(Match(), new Where(
            new FieldFilter(F("f"), FilterOperator.In, new IntegerValue(1)))).Code);

    // ── C1+M1: Cumulative output-name + reserved-AS validation ───────────────

    [Fact]
    public void AsCollidingWithDocumentColumn_Throws() =>
        Assert.Equal("DUPLICATE_AS", Throws(Match(), new Unwind(F("x"), "data")).Code);

    [Fact]
    public void AsCollidingWithEarlierStageOutput_Throws() =>
        Assert.Equal("DUPLICATE_AS", Throws(Match(),
            new Lookup("u", F("a"), F("b"), "j"),
            new Unwind(F("j"), "j")).Code);

    [Fact]
    public void NameAsOutput_Throws() =>
        Assert.Equal("AS_NAME", Throws(Match(), new Unwind(F("x"), "__name__")).Code);

    [Fact]
    public void GroupResetsNamespace_ReusingDocColumnNameIsFine()
    {
        // after group, the row shape is replaced — "data" is a legal output name again
        PipelineNormalizer.Normalize(new Pipeline([Match(),
            new Group([new Ast.GroupKey("data", F("x"))], [new Ast.Accumulator("n", AggFunction.Count)])]));
    }

    // ── M2: Validate lookup collection path ──────────────────────────────────

    [Fact]
    public void LookupBadCollectionPath_Throws() =>
        Assert.Equal("BAD_COLLECTION_PATH", Throws(Match(), new Lookup("u/x", F("a"), F("b"), "j")).Code);
}
