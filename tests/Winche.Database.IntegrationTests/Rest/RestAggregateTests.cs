using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Winche.Database.Documents;
using Winche.Database.Values;
using Winche.Rules;
using Winche.Rules.Expressions;

namespace Winche.Database.IntegrationTests.Rest;

[Collection("postgres")]
public class RestAggregateTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private Task<RestTestHost> AllowAllHost() => RestTestHost.StartAsync(Fx.ConnectionString,
        c => c.UseRules(r => r.Match("{document=**}", b => b.Allow(RuleOperations.All, Expr.Const(true)))));

    private Task<RestTestHost> DenyAllHost() => RestTestHost.StartAsync(Fx.ConnectionString);

    private static async Task<(HttpStatusCode Status, JsonObject Body)> PostAsync(HttpClient client, string route, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PostAsync(route, content);
        var text = await response.Content.ReadAsStringAsync();
        var body = (JsonObject?)(text.Length > 0 ? JsonNode.Parse(text) : null) ?? new JsonObject();
        return (response.StatusCode, body);
    }

    private async Task Seed(string id, params (string K, Value V)[] e)
    {
        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        await new DocumentOperations(conn, null).SetAsync($"c/{id}", e.ToDictionary(x => x.K, x => x.V));
    }

    [Fact]
    public async Task Aggregate_CountAndSum()
    {
        await Seed("a", ("n", new IntegerValue(2)));
        await Seed("b", ("n", new IntegerValue(3)));

        await using var host = await AllowAllHost();
        var (status, body) = await PostAsync(host.Client, "/documents:aggregate",
            """{"query":{"collection":"c"},"aggregations":[{"kind":"count","alias":"cnt"},{"kind":"sum","field":"n","alias":"s"}]}""");

        Assert.Equal(HttpStatusCode.OK, status);
        var result = body["result"]!.AsObject();
        Assert.Equal("2", (string?)result["cnt"]!["integerValue"]);
        Assert.Equal("5", (string?)result["s"]!["integerValue"]);
    }

    [Fact]
    public async Task Aggregate_DenyAll_PermissionDenied()
    {
        await using var host = await DenyAllHost();
        var (status, body) = await PostAsync(host.Client, "/documents:aggregate",
            """{"query":{"collection":"c"},"aggregations":[{"kind":"count","alias":"cnt"}]}""");

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("PERMISSION_DENIED", (string?)body["status"]);
    }

    [Fact]
    public async Task Aggregate_Average()
    {
        await Seed("a", ("n", new IntegerValue(2)));
        await Seed("b", ("n", new IntegerValue(4)));

        await using var host = await AllowAllHost();
        var (status, body) = await PostAsync(host.Client, "/documents:aggregate",
            """{"query":{"collection":"c"},"aggregations":[{"kind":"average","field":"n","alias":"avg"}]}""");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(3.0, (double?)body["result"]!["avg"]!["doubleValue"]);
    }

    [Fact]
    public async Task Aggregate_EmptyCollection_CountZero()
    {
        await using var host = await AllowAllHost();
        var (status, body) = await PostAsync(host.Client, "/documents:aggregate",
            """{"query":{"collection":"nope"},"aggregations":[{"kind":"count","alias":"cnt"}]}""");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("0", (string?)body["result"]!["cnt"]!["integerValue"]);
    }

    [Fact]
    public async Task Aggregate_InvalidKind_BadRequest()
    {
        await using var host = await AllowAllHost();
        var (status, body) = await PostAsync(host.Client, "/documents:aggregate",
            """{"query":{"collection":"c"},"aggregations":[{"kind":"median","alias":"x"}]}""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("INVALID_ARGUMENT", (string?)body["status"]);
    }
}
