using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Winche.Database.DependencyInjection;
using Winche.Database.Querying.Planning;
using Winche.Rules;
using Winche.Rules.Expressions;

namespace Winche.Database.IntegrationTests.Rest;

/// <summary>
/// Covers the configurable request-shape ceilings enforced at the REST boundary:
/// <see cref="QueryLimits"/> for <c>:runQuery</c>/<c>:count</c>/<c>:aggregate</c> and
/// <see cref="QueryLimits.MaxBatchGetSize"/> for <c>:batchGet</c>.
/// </summary>
[Collection("postgres")]
public class RestRequestLimitsTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private Task<RestTestHost> HostWithLimits(QueryLimits limits) => RestTestHost.StartAsync(Fx.ConnectionString,
        c =>
        {
            c.UseRules(r => r.Match("{document=**}", b => b.Allow(RuleOperations.All, Expr.Const(true))));
            c.QueryLimits = limits;
        });

    private static async Task<(HttpStatusCode Status, JsonObject Body)> PostAsync(HttpClient client, string route, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PostAsync(route, content);
        var text = await response.Content.ReadAsStringAsync();
        var body = (JsonObject?)(text.Length > 0 ? JsonNode.Parse(text) : null) ?? new JsonObject();
        return (response.StatusCode, body);
    }

    [Fact]
    public async Task BatchGet_AtLimit_Succeeds_OverLimit_Rejected()
    {
        await using var host = await HostWithLimits(new QueryLimits { MaxBatchGetSize = 3 });
        var client = host.Client;

        var atLimit = """{"documents": ["a/1", "a/2", "a/3"]}""";
        var (atStatus, _) = await PostAsync(client, "/documents:batchGet", atLimit);
        Assert.Equal(HttpStatusCode.OK, atStatus);

        var overLimit = """{"documents": ["a/1", "a/2", "a/3", "a/4"]}""";
        var (overStatus, overBody) = await PostAsync(client, "/documents:batchGet", overLimit);
        Assert.Equal(HttpStatusCode.BadRequest, overStatus);
        Assert.Equal("INVALID_ARGUMENT", (string?)overBody["status"]);
    }

    [Fact]
    public async Task RunQuery_LimitAtCeiling_Succeeds_OverCeiling_Rejected()
    {
        await using var host = await HostWithLimits(new QueryLimits { MaxLimit = 5 });
        var client = host.Client;

        var (atStatus, _) = await PostAsync(client, "/documents:runQuery",
            """{"query": {"collection": "c", "limit": 5}}""");
        Assert.Equal(HttpStatusCode.OK, atStatus);

        var (overStatus, overBody) = await PostAsync(client, "/documents:runQuery",
            """{"query": {"collection": "c", "limit": 6}}""");
        Assert.Equal(HttpStatusCode.BadRequest, overStatus);
        Assert.Equal("INVALID_QUERY", (string?)overBody["status"]);
        Assert.Equal("LIMIT_TOO_LARGE", (string?)overBody["details"]?["code"]);
    }

    [Fact]
    public async Task RunQuery_OrderByOverCeiling_Rejected()
    {
        await using var host = await HostWithLimits(new QueryLimits { MaxOrderByKeys = 2 });
        var client = host.Client;

        var (okStatus, _) = await PostAsync(client, "/documents:runQuery",
            """{"query": {"collection": "c", "orderBy": [{"field": "a"}, {"field": "b"}]}}""");
        Assert.Equal(HttpStatusCode.OK, okStatus);

        var (overStatus, overBody) = await PostAsync(client, "/documents:runQuery",
            """{"query": {"collection": "c", "orderBy": [{"field": "a"}, {"field": "b"}, {"field": "d"}]}}""");
        Assert.Equal(HttpStatusCode.BadRequest, overStatus);
        Assert.Equal("INVALID_QUERY", (string?)overBody["status"]);
        Assert.Equal("TOO_MANY_ORDER_BY_KEYS", (string?)overBody["details"]?["code"]);
    }

    [Fact]
    public async Task RunQuery_InOperandOverCeiling_Rejected()
    {
        await using var host = await HostWithLimits(new QueryLimits { MaxArrayOperandCount = 2 });
        var client = host.Client;

        var (okStatus, _) = await PostAsync(client, "/documents:runQuery",
            """{"query": {"collection": "c", "where": {"field": "n", "op": "in", "value": {"arrayValue": {"values": [{"integerValue": "1"}, {"integerValue": "2"}]}}}}}""");
        Assert.Equal(HttpStatusCode.OK, okStatus);

        var (overStatus, overBody) = await PostAsync(client, "/documents:runQuery",
            """{"query": {"collection": "c", "where": {"field": "n", "op": "in", "value": {"arrayValue": {"values": [{"integerValue": "1"}, {"integerValue": "2"}, {"integerValue": "3"}]}}}}}""");
        Assert.Equal(HttpStatusCode.BadRequest, overStatus);
        Assert.Equal("INVALID_QUERY", (string?)overBody["status"]);
        Assert.Equal("TOO_MANY_OPERANDS", (string?)overBody["details"]?["code"]);
    }

    [Fact]
    public async Task Count_OverCeiling_Rejected()
    {
        await using var host = await HostWithLimits(new QueryLimits { MaxOffset = 1 });
        var client = host.Client;

        var (status, body) = await PostAsync(client, "/documents:count",
            """{"query": {"collection": "c", "offset": 2}}""");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("INVALID_QUERY", (string?)body["status"]);
        Assert.Equal("OFFSET_TOO_LARGE", (string?)body["details"]?["code"]);
    }

    [Fact]
    public async Task Aggregate_OverCeiling_Rejected()
    {
        await using var host = await HostWithLimits(new QueryLimits { MaxLimit = 1 });
        var client = host.Client;

        var (status, body) = await PostAsync(client, "/documents:aggregate",
            """{"query": {"collection": "c", "limit": 2}, "aggregations": [{"kind": "count", "alias": "cnt"}]}""");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("INVALID_QUERY", (string?)body["status"]);
        Assert.Equal("LIMIT_TOO_LARGE", (string?)body["details"]?["code"]);
    }
}
