using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class PipelineSmokeTests(PostgresFixture fx) : PipelineTestBase(fx)
{
    [Fact]
    public async Task MatchOnly_ReturnsDocumentRows_WithNameAndFields()
    {
        await SeedDoc("a", new Dictionary<string, Value> { ["x"] = I(1) });
        await SeedDoc("other", new Dictionary<string, Value>(), collection: "elsewhere");

        var result = await RunPipeline(new MatchStageAst("c", null));

        var row = Assert.Single(result.Rows);
        Assert.Equal(I(1), row["x"]);
        Assert.Equal(new ReferenceValue("c/a"), row["__name__"]);
    }

    [Fact]
    public async Task MatchWhere_Filters()
    {
        await SeedDoc("a", new Dictionary<string, Value> { ["x"] = I(1) });
        await SeedDoc("b", new Dictionary<string, Value> { ["x"] = I(2) });

        var result = await RunPipeline(new MatchStageAst("c",
            new FieldFilterAst(F("x"), FilterOperator.Gt, I(1))));

        Assert.Equal(new ReferenceValue("c/b"), Assert.Single(result.Rows)["__name__"]);
    }

    [Fact]
    public async Task FilterSortSkipLimit_ComposeOnDocuments()
    {
        for (var i = 1; i <= 6; i++)
            await SeedDoc($"d{i}", new Dictionary<string, Value> { ["x"] = I(i) });

        var result = await RunPipeline(
            new MatchStageAst("c", null),
            new FilterStageAst(new FieldFilterAst(F("x"), FilterOperator.Gte, I(2))),   // 2..6
            new SortStageAst([new OrderAst(F("x"), SortDirection.Desc)]),               // 6,5,4,3,2
            new SkipStageAst(1),                                                        // 5,4,3,2
            new LimitStageAst(2));                                                      // 5,4

        Assert.Equal([5L, 4L], result.Rows.Select(r => ((IntegerValue)r["x"]).Value));
    }

    [Fact]
    public async Task MidPipelineSortLimit_ThenFilter_PreservesOrderSemantics()
    {
        for (var i = 1; i <= 5; i++)
            await SeedDoc($"d{i}", new Dictionary<string, Value> { ["x"] = I(i) });

        // top-3 by x desc, then keep odd values → 5, 3
        var result = await RunPipeline(
            new MatchStageAst("c", null),
            new SortStageAst([new OrderAst(F("x"), SortDirection.Desc)]),
            new LimitStageAst(3),
            new FilterStageAst(new FieldFilterAst(F("x"), FilterOperator.In,
                new ArrayValue([I(1), I(3), I(5)]))),
            new SortStageAst([new OrderAst(F("x"), SortDirection.Desc)]));

        Assert.Equal([5L, 3L], result.Rows.Select(r => ((IntegerValue)r["x"]).Value));
    }

    [Fact]
    public async Task ParseNormalizeExecute_EndToEndFromWireJson()
    {
        await SeedDoc("a", new Dictionary<string, Value> { ["x"] = I(42) });

        var json = """{"pipeline":[{"match":{"collection":"c","where":{"field":"x","op":"gt","value":{"integerValue":"10"}}}}]}""";
        var ast = Winche.Database.Querying.Ast.PipelineParser.Parse(
            (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(json)!);

        var result = await RunPipeline([.. ast.Stages]);
        Assert.Single(result.Rows);
    }
}
