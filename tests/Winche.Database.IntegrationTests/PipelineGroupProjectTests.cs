using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class PipelineGroupProjectTests(PostgresFixture fx) : PipelineTestBase(fx)
{
    private async Task SeedSales()
    {
        await SeedDoc("s1", new Dictionary<string, Value> { ["city"] = S("Oslo"), ["amt"] = I(10) });
        await SeedDoc("s2", new Dictionary<string, Value> { ["city"] = S("Oslo"), ["amt"] = D(2.5) });
        await SeedDoc("s3", new Dictionary<string, Value> { ["city"] = S("Bergen"), ["amt"] = I(7) });
        await SeedDoc("s4", new Dictionary<string, Value> { ["city"] = S("Bergen") });           // amt missing
    }

    private static Group Group(params Accumulator[] accs) =>
        new([new GroupKey("city", FieldPath.Parse("city"))], accs);

    private async Task<Dictionary<string, IReadOnlyDictionary<string, Value>>> RunGrouped(params Accumulator[] accs)
    {
        var result = await RunPipeline(new Match("c", null), Group(accs));
        return result.Rows.ToDictionary(r => ((StringValue)r["city"]).Value, r => r);
    }

    [Fact]
    public async Task Count_PerGroup()
    {
        await SeedSales();
        var rows = await RunGrouped(new Accumulator("n", AggFunction.Count));
        Assert.Equal(new IntegerValue(2), rows["Oslo"]["n"]);
        Assert.Equal(new IntegerValue(2), rows["Bergen"]["n"]);
    }

    [Fact]
    public async Task Sum_MixesIntAndDouble_MissingExcluded()
    {
        await SeedSales();
        var rows = await RunGrouped(new Accumulator("t", AggFunction.Sum, FieldPath.Parse("amt")));
        Assert.Equal(new DoubleValue(12.5), rows["Oslo"]["t"]);
        Assert.Equal(new DoubleValue(7), rows["Bergen"]["t"]);
    }

    [Fact]
    public async Task MinMax_ReturnOriginalTaggedValues()
    {
        await SeedSales();
        var rows = await RunGrouped(
            new Accumulator("lo", AggFunction.Min, FieldPath.Parse("amt")),
            new Accumulator("hi", AggFunction.Max, FieldPath.Parse("amt")));
        Assert.Equal(new DoubleValue(2.5), rows["Oslo"]["lo"]);   // the ORIGINAL double, not a coerced value
        Assert.Equal(new IntegerValue(10), rows["Oslo"]["hi"]);
        Assert.Equal(new IntegerValue(7), rows["Bergen"]["lo"]);  // missing amt (s4) excluded
    }

    [Fact]
    public async Task PushAndAddToSet()
    {
        await SeedDoc("a", new Dictionary<string, Value> { ["g"] = S("x"), ["v"] = I(1) });
        await SeedDoc("b", new Dictionary<string, Value> { ["g"] = S("x"), ["v"] = I(1) });
        await SeedDoc("d", new Dictionary<string, Value> { ["g"] = S("x"), ["v"] = I(2) });

        var result = await RunPipeline(new Match("c", null),
            new Group([new GroupKey("g", F("g"))],
            [
                new Accumulator("all", AggFunction.Push, F("v")),
                new Accumulator("set", AggFunction.AddToSet, F("v")),
            ]));

        var row = Assert.Single(result.Rows);
        Assert.Equal(3, Assert.IsType<ArrayValue>(row["all"]).Values.Count);
        Assert.Equal(2, Assert.IsType<ArrayValue>(row["set"]).Values.Count);
    }

    [Fact]
    public async Task GroupKey_IntAndDoubleGroupTogether()
    {
        await SeedDoc("a", new Dictionary<string, Value> { ["k"] = I(5) });
        await SeedDoc("b", new Dictionary<string, Value> { ["k"] = D(5.0) });

        var result = await RunPipeline(new Match("c", null),
            new Group([new GroupKey("k", F("k"))], [new Accumulator("n", AggFunction.Count)]));

        var row = Assert.Single(result.Rows);                     // ONE group (winche_key equality)
        Assert.Equal(new IntegerValue(2), row["n"]);
    }

    [Fact]
    public async Task GlobalGroup_NoKeys()
    {
        await SeedSales();
        var result = await RunPipeline(new Match("c", null),
            new Group([], [new Accumulator("n", AggFunction.Count)]));
        Assert.Equal(new IntegerValue(4), Assert.Single(result.Rows)["n"]);
    }

    [Fact]
    public async Task Having_FiltersGroups()
    {
        await SeedSales();
        var result = await RunPipeline(new Match("c", null),
            new Group([new GroupKey("city", F("city"))],
                [new Accumulator("t", AggFunction.Sum, F("amt"))],
                Having: new FieldFilter(F("t"), FilterOperator.Gt, D(10))));

        Assert.Equal(S("Oslo"), Assert.Single(result.Rows)["city"]);
    }

    [Fact]
    public async Task SortByAccumulator_AfterGroup()
    {
        await SeedSales();
        var result = await RunPipeline(new Match("c", null),
            new Group([new GroupKey("city", F("city"))],
                [new Accumulator("t", AggFunction.Sum, F("amt"))]),
            new Sort([new Ordering(F("t"), SortDirection.Desc)]));

        Assert.Equal(["Oslo", "Bergen"], result.Rows.Select(r => ((StringValue)r["city"]).Value));
    }

    // ── Project ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Project_FieldLiteralAndWindowAggregate()
    {
        await SeedSales();
        var result = await RunPipeline(new Match("c", null),
            new Where(new UnaryFilter(F("amt"), UnaryOp.Exists)),
            new Project(
            [
                new Projection("city", new FieldRefExpr(F("city"))),
                new Projection("ver", new LiteralExpr(S("v1"))),
                new Projection("grand", new AggFuncExpr(AggFunction.Sum, F("amt"))),
            ]));

        Assert.Equal(3, result.Rows.Count);
        Assert.All(result.Rows, r =>
        {
            Assert.Equal(S("v1"), r["ver"]);
            Assert.Equal(new DoubleValue(19.5), r["grand"]);      // windowed sum over all rows
            Assert.False(r.ContainsKey("amt"));                   // projection drops other columns
        });
    }

    [Fact]
    public async Task Project_NestedFieldExtraction()
    {
        await SeedDoc("a", new Dictionary<string, Value>
        {
            ["addr"] = new MapValue(new Dictionary<string, Value> { ["city"] = S("Oslo") }),
        });

        var result = await RunPipeline(new Match("c", null),
            new Project([new Projection("city", new FieldRefExpr(F("addr.city")))]));

        Assert.Equal(S("Oslo"), Assert.Single(result.Rows)["city"]);
    }

    // ── M5: Additional coverage ──────────────────────────────────────────────

    [Fact]
    public async Task Avg_AndAvgOfNothing()
    {
        await SeedDoc("a", new Dictionary<string, Value> { ["g"] = S("x"), ["v"] = I(2) });
        await SeedDoc("b", new Dictionary<string, Value> { ["g"] = S("x"), ["v"] = I(4) });
        await SeedDoc("d", new Dictionary<string, Value> { ["g"] = S("y") });          // no v

        var result = await RunPipeline(new Match("c", null),
            new Group([new GroupKey("g", F("g"))], [new Accumulator("m", AggFunction.Avg, F("v"))]),
            new Sort([new Ordering(F("g"))]));

        Assert.Equal(new DoubleValue(3), result.Rows[0]["m"]);
        Assert.Equal(new NullValue(), result.Rows[1]["m"]);       // avg of nothing = nullValue
    }

    [Fact]
    public async Task FirstLast_AfterSort_AreDeterministic()
    {
        await SeedDoc("a", new Dictionary<string, Value> { ["v"] = I(1) });
        await SeedDoc("b", new Dictionary<string, Value> { ["v"] = I(2) });
        await SeedDoc("d", new Dictionary<string, Value> { ["v"] = I(3) });

        var result = await RunPipeline(new Match("c", null),
            new Sort([new Ordering(F("v"))]),
            new Group([], [
                new Accumulator("fst", AggFunction.First, F("v")),
                new Accumulator("lst", AggFunction.Last, F("v"))]));

        var row = Assert.Single(result.Rows);
        Assert.Equal(I(1), row["fst"]);
        Assert.Equal(I(3), row["lst"]);
    }

    [Fact]
    public async Task Sum_OverNonNumericValues_IsZero()
    {
        await SeedDoc("a", new Dictionary<string, Value> { ["v"] = S("notANumber") });
        var result = await RunPipeline(new Match("c", null),
            new Group([], [new Accumulator("t", AggFunction.Sum, F("v"))]));
        Assert.Equal(new DoubleValue(0), Assert.Single(result.Rows)["t"]);   // pins the M3 doc'd choice
    }

    [Fact]
    public async Task FilterOnUnknownRowColumn_MatchesNothing()
    {
        await SeedDoc("a", new Dictionary<string, Value> { ["v"] = I(1) });
        var result = await RunPipeline(new Match("c", null),
            new Group([], [new Accumulator("n", AggFunction.Count)]),
            new Where(new FieldFilter(F("bogus"), FilterOperator.Eq, I(1))));
        Assert.Empty(result.Rows);
    }
}
