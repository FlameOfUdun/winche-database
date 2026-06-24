using System.Text.Json.Nodes;
using Winche.Database.Documents;
using Winche.Database.Values;
using Winche.Rules;
using Winche.Rules.Expressions;

namespace Winche.Database.IntegrationTests.Ws;

[Collection("postgres")]
public class WsAggregateTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private Task<WsTestHost> AllowAllHost() => WsTestHost.StartAsync(Fx.ConnectionString,
        c => c.UseRules(r => r.Match("{document=**}", b => b.Allow(RuleOperations.All, Expr.Const(true)))));

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
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server);

        var resp = await ws.RequestAsync(new JsonObject
        {
            ["type"] = "aggregate",
            ["query"] = new JsonObject { ["collection"] = "c" },
            ["aggregations"] = new JsonArray(
                new JsonObject { ["kind"] = "count", ["alias"] = "cnt" },
                new JsonObject { ["kind"] = "sum", ["field"] = "n", ["alias"] = "s" }),
        });

        Assert.Equal("response", (string?)resp["type"]);
        var result = resp["result"]!["result"]!.AsObject();
        Assert.Equal("2", (string?)result["cnt"]!["integerValue"]);
        Assert.Equal("5", (string?)result["s"]!["integerValue"]);
    }

    [Fact]
    public async Task Aggregate_DenyAll_PermissionDenied()
    {
        await using var host = await WsTestHost.StartAsync(Fx.ConnectionString); // deny-all
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server);

        var resp = await ws.RequestAsync(new JsonObject
        {
            ["type"] = "aggregate",
            ["query"] = new JsonObject { ["collection"] = "c" },
            ["aggregations"] = new JsonArray(new JsonObject { ["kind"] = "count", ["alias"] = "cnt" }),
        });

        Assert.Equal("PERMISSION_DENIED", (string?)resp["status"]);
    }

    [Fact]
    public async Task Aggregate_MalformedKind_InvalidArgument()
    {
        await using var host = await AllowAllHost();
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server);

        var resp = await ws.RequestAsync(new JsonObject
        {
            ["type"] = "aggregate",
            ["query"] = new JsonObject { ["collection"] = "c" },
            ["aggregations"] = new JsonArray(new JsonObject { ["kind"] = "BOGUS", ["alias"] = "x" }),
        });

        Assert.Equal("INVALID_ARGUMENT", (string?)resp["status"]);
    }
}
