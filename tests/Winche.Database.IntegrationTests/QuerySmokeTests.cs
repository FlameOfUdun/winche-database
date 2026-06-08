using Winche.Database.Querying.Ast;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class QuerySmokeTests(PostgresFixture fx) : QueryTestBase(fx)
{
    [Fact]
    public async Task NoFilter_ReturnsWholeCollection_InNameOrder()
    {
        await Seed("b", new IntegerValue(1));
        await Seed("a", new IntegerValue(2));
        await SeedDoc("other", new Dictionary<string, Value>(), collection: "elsewhere");

        var ids = await Ids(new Query("c"));
        Assert.Equal(["a", "b"], ids);                  // __name__ ASC default; other collection excluded
    }

    [Fact]
    public async Task WhereOrderLimit_WorkTogether()
    {
        for (var i = 1; i <= 5; i++)
            await Seed($"d{i}", new IntegerValue(i));

        var result = await Run(new Query("c",
            Where: new FieldFilter(F("f"), FilterOperator.Gte, new IntegerValue(2)),
            OrderBy: [new Ordering(F("f"), SortDirection.Desc)],
            Limit: 3));

        Assert.Equal(["d5", "d4", "d3"], result.Documents.Select(d => d.Id));
        Assert.True(result.HasMore);                    // d2 also matches
    }

    [Fact]
    public async Task HasMore_FalseWhenExactlyLimit()
    {
        await Seed("a", new IntegerValue(1));
        await Seed("b", new IntegerValue(2));
        var result = await Run(new Query("c", Limit: 2));
        Assert.False(result.HasMore);
        Assert.Equal(2, result.Documents.Count);
    }

    [Fact]
    public async Task NestedFieldFilter_Works()
    {
        await SeedDoc("u1", new Dictionary<string, Value>
        {
            ["address"] = new MapValue(new Dictionary<string, Value> { ["city"] = new StringValue("Oslo") }),
        });
        await SeedDoc("u2", new Dictionary<string, Value>
        {
            ["address"] = new MapValue(new Dictionary<string, Value> { ["city"] = new StringValue("Bergen") }),
        });

        var ids = await Ids(new Query("c",
            Where: new FieldFilter(F("address.city"), FilterOperator.Eq, new StringValue("Oslo"))));
        Assert.Equal(["u1"], ids);
    }

    [Fact]
    public async Task ParseNormalizeExecute_EndToEndFromWireJson()
    {
        await Seed("x", new IntegerValue(42));
        await Seed("y", new IntegerValue(7));

        var json = """
            {"collection":"c","where":{"field":"f","op":"gt","value":{"integerValue":"10"}}}
            """;
        var ast = Winche.Database.Querying.Ast.QueryParser.Parse(
            (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(json)!);

        Assert.Equal(["x"], await Ids(ast));
    }
}
