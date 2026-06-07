// tests/Winche.Database.Tests/Querying/PipelineNormalizerTests.cs
using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Planning;
using Winche.Database.Values;

namespace Winche.Database.Tests.Querying;

public class PipelineNormalizerTests
{
    private static FieldPath F(string p) => FieldPath.Parse(p);
    private static MatchStageAst Match(string c = "orders") => new(c, null);

    private static PlanValidationException Throws(params StageAst[] stages) =>
        Assert.Throws<PlanValidationException>(() => PipelineNormalizer.Normalize(new PipelineAst(stages)));

    [Fact]
    public void Match_BecomesScanPlusFilter()
    {
        var where = new FieldFilterAst(F("s"), FilterOperator.Eq, new BooleanValue(true));
        var plan = PipelineNormalizer.Normalize(new PipelineAst([new MatchStageAst("orders", where)]));

        Assert.Equal("orders", Assert.IsType<CollectionScan>(plan.Nodes[0]).Collection);
        Assert.Equal(where, Assert.IsType<FilterNode>(plan.Nodes[1]).Predicate);
        Assert.Equal(2, plan.Nodes.Count);                       // no Sort/Page added — pipelines are explicit
    }

    [Fact]
    public void Match_EqNullRewriteApplies()
    {
        var plan = PipelineNormalizer.Normalize(new PipelineAst(
            [new MatchStageAst("o", new FieldFilterAst(F("x"), FilterOperator.Eq, new NullValue()))]));
        var unary = Assert.IsType<UnaryFilterAst>(Assert.IsType<FilterNode>(plan.Nodes[1]).Predicate);
        Assert.Equal(UnaryOp.IsNull, unary.Op);
    }

    [Fact]
    public void Having_BecomesFilterAfterGroup()
    {
        var plan = PipelineNormalizer.Normalize(new PipelineAst(
        [
            Match(),
            new GroupStageAst(
                [new GroupKeyAst("city", F("addr.city"))],
                [new AccumulatorAst("n", AggFunction.Count)],
                Having: new FieldFilterAst(F("n"), FilterOperator.Gt, new IntegerValue(1))),
        ]));

        Assert.IsType<CollectionScan>(plan.Nodes[0]);
        Assert.IsType<GroupNode>(plan.Nodes[1]);
        Assert.IsType<FilterNode>(plan.Nodes[2]);
    }

    [Fact]
    public void AllStages_MapToNodes()
    {
        var plan = PipelineNormalizer.Normalize(new PipelineAst(
        [
            Match(),
            new FilterStageAst(new UnaryFilterAst(F("x"), UnaryOp.Exists)),
            new LookupStageAst("users", F("uid"), F("__name__"), "user"),
            new UnwindStageAst(F("items"), "item"),
            new GroupStageAst([], [new AccumulatorAst("n", AggFunction.Count)]),
            new ProjectStageAst([new ProjectionAst("n", new FieldRefExprAst(F("n")))]),
            new SortStageAst([new OrderAst(F("n"), SortDirection.Desc)]),
            new SkipStageAst(2),
            new LimitStageAst(7),
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
        Assert.Equal("MATCH_FIRST", Throws(new LimitStageAst(1)).Code);

    [Fact]
    public void MatchNotFirstOnly_Throws() =>
        Assert.Equal("MATCH_FIRST", Throws(Match(), Match()).Code);

    [Fact]
    public void BadCollectionPath_Throws() =>
        Assert.Equal("BAD_COLLECTION_PATH", Throws(new MatchStageAst("users/u1", null)).Code);

    [Theory]
    [InlineData("1bad")]
    [InlineData("has space")]
    [InlineData("quo\"te")]
    [InlineData("")]
    public void BadAsName_Throws(string name) =>
        Assert.Equal("AS_NAME", Throws(Match(), new UnwindStageAst(F("x"), name)).Code);

    [Fact]
    public void DuplicateAs_InGroup_Throws() =>
        Assert.Equal("DUPLICATE_AS", Throws(Match(), new GroupStageAst(
            [new GroupKeyAst("n", F("a"))],
            [new AccumulatorAst("n", AggFunction.Count)])).Code);

    [Fact]
    public void AccumulatorNeedsField_ExceptCount() =>
        Assert.Equal("ACC_FIELD", Throws(Match(), new GroupStageAst(
            [], [new AccumulatorAst("s", AggFunction.Sum)])).Code);

    [Fact]
    public void ProjectAgg_OnlyCountSumAvg()
    {
        Assert.Equal("PROJECT_AGG", Throws(Match(), new ProjectStageAst(
            [new ProjectionAst("m", new AggFuncExprAst(AggFunction.Min, F("x")))])).Code);
        // count/sum/avg fine:
        PipelineNormalizer.Normalize(new PipelineAst([Match(), new ProjectStageAst(
            [new ProjectionAst("c", new AggFuncExprAst(AggFunction.Count))])]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveLimit_Throws(int n) =>
        Assert.Equal("BAD_LIMIT", Throws(Match(), new LimitStageAst(n)).Code);

    [Fact]
    public void NegativeSkip_Throws() =>
        Assert.Equal("BAD_SKIP", Throws(Match(), new SkipStageAst(-1)).Code);

    [Fact]
    public void LookupBadLimit_Throws() =>
        Assert.Equal("BAD_LIMIT", Throws(Match(),
            new LookupStageAst("u", F("a"), F("b"), "x", Limit: 0)).Code);

    [Fact]
    public void FilterOperandValidation_AppliesInsidePipeline() =>
        Assert.Equal("OPERAND_TYPE", Throws(Match(), new FilterStageAst(
            new FieldFilterAst(F("f"), FilterOperator.In, new IntegerValue(1)))).Code);

    // ── C1+M1: Cumulative output-name + reserved-AS validation ───────────────

    [Fact]
    public void AsCollidingWithDocumentColumn_Throws() =>
        Assert.Equal("DUPLICATE_AS", Throws(Match(), new UnwindStageAst(F("x"), "data")).Code);

    [Fact]
    public void AsCollidingWithEarlierStageOutput_Throws() =>
        Assert.Equal("DUPLICATE_AS", Throws(Match(),
            new LookupStageAst("u", F("a"), F("b"), "j"),
            new UnwindStageAst(F("j"), "j")).Code);

    [Fact]
    public void NameAsOutput_Throws() =>
        Assert.Equal("AS_NAME", Throws(Match(), new UnwindStageAst(F("x"), "__name__")).Code);

    [Fact]
    public void GroupResetsNamespace_ReusingDocColumnNameIsFine()
    {
        // after group, the row shape is replaced — "data" is a legal output name again
        PipelineNormalizer.Normalize(new PipelineAst([Match(),
            new GroupStageAst([new GroupKeyAst("data", F("x"))], [new AccumulatorAst("n", AggFunction.Count)])]));
    }

    // ── M2: Validate lookup collection path ──────────────────────────────────

    [Fact]
    public void LookupBadCollectionPath_Throws() =>
        Assert.Equal("BAD_COLLECTION_PATH", Throws(Match(), new LookupStageAst("u/x", F("a"), F("b"), "j")).Code);
}
