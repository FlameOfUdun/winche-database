using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class PipelineComposedTests(PostgresFixture fx) : PipelineTestBase(fx)
{
    [Fact]
    public async Task LookupUnwindGroup_CrossCollectionAggregation()
    {
        await SeedDoc("u1", new Dictionary<string, Value> { ["city"] = S("Oslo") }, collection: "users");
        await SeedDoc("u2", new Dictionary<string, Value> { ["city"] = S("Bergen") }, collection: "users");
        await SeedDoc("o1", new Dictionary<string, Value> { ["uid"] = new ReferenceValue("users/u1"), ["amt"] = I(10) }, collection: "orders");
        await SeedDoc("o2", new Dictionary<string, Value> { ["uid"] = new ReferenceValue("users/u1"), ["amt"] = I(5) }, collection: "orders");
        await SeedDoc("o3", new Dictionary<string, Value> { ["uid"] = new ReferenceValue("users/u2"), ["amt"] = I(7) }, collection: "orders");

        // revenue per user city: orders → lookup user → unwind → group by user.city
        var result = await RunPipeline(
            new MatchStageAst("orders", null),
            new LookupStageAst("users", F("uid"), F("__name__"), "user"),
            new UnwindStageAst(F("user"), "u"),
            new GroupStageAst([new GroupKeyAst("city", F("u.city"))],
                [new AccumulatorAst("revenue", AggFunction.Sum, F("amt"))]),
            new SortStageAst([new OrderAst(F("revenue"), SortDirection.Desc)]));

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(S("Oslo"), result.Rows[0]["city"]);
        Assert.Equal(new DoubleValue(15), result.Rows[0]["revenue"]);
        Assert.Equal(S("Bergen"), result.Rows[1]["city"]);
        Assert.Equal(new DoubleValue(7), result.Rows[1]["revenue"]);
    }

    [Fact]
    public async Task GroupAfterProject_TheOldEngineKiller()
    {
        await SeedDoc("a", new Dictionary<string, Value>
            { ["m"] = new MapValue(new Dictionary<string, Value> { ["cat"] = S("x") }), ["v"] = I(1) });
        await SeedDoc("b", new Dictionary<string, Value>
            { ["m"] = new MapValue(new Dictionary<string, Value> { ["cat"] = S("x") }), ["v"] = I(2) });
        await SeedDoc("d", new Dictionary<string, Value>
            { ["m"] = new MapValue(new Dictionary<string, Value> { ["cat"] = S("y") }), ["v"] = I(9) });

        var result = await RunPipeline(
            new MatchStageAst("c", null),
            new ProjectStageAst(
            [
                new ProjectionAst("cat", new FieldRefExprAst(F("m.cat"))),
                new ProjectionAst("v", new FieldRefExprAst(F("v"))),
            ]),
            new GroupStageAst([new GroupKeyAst("cat", F("cat"))],
                [new AccumulatorAst("total", AggFunction.Sum, F("v"))]),
            new SortStageAst([new OrderAst(F("cat"))]));

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(new DoubleValue(3), result.Rows[0]["total"]);
        Assert.Equal(new DoubleValue(9), result.Rows[1]["total"]);
    }

    [Fact]
    public async Task StackedGroups_GroupOfGroups()
    {
        // sales: (region, city, amt) → per-city totals → per-region city count + max city total
        await SeedDoc("s1", new Dictionary<string, Value> { ["region"] = S("EU"), ["city"] = S("Oslo"), ["amt"] = I(10) });
        await SeedDoc("s2", new Dictionary<string, Value> { ["region"] = S("EU"), ["city"] = S("Oslo"), ["amt"] = I(5) });
        await SeedDoc("s3", new Dictionary<string, Value> { ["region"] = S("EU"), ["city"] = S("Bergen"), ["amt"] = I(7) });
        await SeedDoc("s4", new Dictionary<string, Value> { ["region"] = S("US"), ["city"] = S("NYC"), ["amt"] = I(99) });

        var result = await RunPipeline(
            new MatchStageAst("c", null),
            new GroupStageAst(
                [new GroupKeyAst("region", F("region")), new GroupKeyAst("city", F("city"))],
                [new AccumulatorAst("cityTotal", AggFunction.Sum, F("amt"))]),
            new GroupStageAst(
                [new GroupKeyAst("region", F("region"))],
                [
                    new AccumulatorAst("cities", AggFunction.Count),
                    new AccumulatorAst("best", AggFunction.Max, F("cityTotal")),
                ]),
            new SortStageAst([new OrderAst(F("region"))]));

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(new IntegerValue(2), result.Rows[0]["cities"]);     // EU: Oslo, Bergen
        Assert.Equal(new DoubleValue(15), result.Rows[0]["best"]);       // Oslo 15
        Assert.Equal(new IntegerValue(1), result.Rows[1]["cities"]);     // US: NYC
    }

    [Fact]
    public async Task HavingThenSortThenLimit_FullChain()
    {
        for (var i = 1; i <= 5; i++)
        {
            await SeedDoc($"g{i}a", new Dictionary<string, Value> { ["g"] = S($"g{i}"), ["v"] = I(i) });
            await SeedDoc($"g{i}b", new Dictionary<string, Value> { ["g"] = S($"g{i}"), ["v"] = I(i) });
        }

        var result = await RunPipeline(
            new MatchStageAst("c", null),
            new GroupStageAst([new GroupKeyAst("g", F("g"))],
                [new AccumulatorAst("t", AggFunction.Sum, F("v"))],
                Having: new FieldFilterAst(F("t"), FilterOperator.Gte, D(4))),   // groups g2..g5 (t = 4,6,8,10)
            new SortStageAst([new OrderAst(F("t"), SortDirection.Desc)]),
            new LimitStageAst(2));

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(new DoubleValue(10), result.Rows[0]["t"]);
        Assert.Equal(new DoubleValue(8), result.Rows[1]["t"]);
    }

    // ── I1: Re-emit ORDER BY for non-adjacent Sort/Limit ────────────────────

    [Fact]
    public async Task SortThenFilterThenLimit_PreservesSortSelection()
    {
        for (var i = 1; i <= 6; i++)
            await SeedDoc($"d{i}", new Dictionary<string, Value> { ["x"] = I(i) });

        // top-2 of (x desc) AFTER filtering out odd values → 6, 4 (NOT arbitrary rows)
        var result = await RunPipeline(
            new MatchStageAst("c", null),
            new SortStageAst([new OrderAst(F("x"), SortDirection.Desc)]),
            new FilterStageAst(new FieldFilterAst(F("x"), FilterOperator.In,
                new ArrayValue([I(2), I(4), I(6)]))),
            new LimitStageAst(2));

        Assert.Equal([6L, 4L], result.Rows.Select(r => ((IntegerValue)r["x"]).Value));
    }

    [Fact]
    public async Task EvilCollectionName_IsInertInMatchAndLookup()
    {
        const string Evil = "x'; DROP TABLE documents; --";
        await SeedDoc("a", new Dictionary<string, Value> { ["x"] = I(1) });

        var viaMatch = await RunPipeline(new MatchStageAst(Evil, null));
        Assert.Empty(viaMatch.Rows);

        var viaLookup = await RunPipeline(
            new MatchStageAst("c", null),
            new LookupStageAst(Evil, F("x"), F("x"), "j"));
        Assert.Empty(Assert.IsType<ArrayValue>(Assert.Single(viaLookup.Rows)["j"]).Values);

        // table intact
        var sanity = await RunPipeline(new MatchStageAst("c", null));
        Assert.Single(sanity.Rows);
    }
}
