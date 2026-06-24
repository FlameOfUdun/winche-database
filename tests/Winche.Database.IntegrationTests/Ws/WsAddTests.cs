using System.Text.Json.Nodes;
using Winche.Rules;
using Winche.Rules.Expressions;

namespace Winche.Database.IntegrationTests.Ws;

[Collection("postgres")]
public class WsAddTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private Task<WsTestHost> AllowAllHost() => WsTestHost.StartAsync(Fx.ConnectionString,
        c => c.UseRules(r => r.Match("{document=**}", b => b.Allow(RuleOperations.All, Expr.Const(true)))));

    [Fact]
    public async Task Add_CreatesDocument_WithGeneratedId()
    {
        await using var host = await AllowAllHost();
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server);

        var resp = await ws.RequestAsync(new JsonObject
        {
            ["type"] = "add",
            ["collection"] = "people",
            ["fields"] = new JsonObject { ["name"] = new JsonObject { ["stringValue"] = "ada" } },
        });

        Assert.Equal("response", (string?)resp["type"]);
        var doc = resp["result"]!["document"]!.AsObject();
        Assert.Equal(20, ((string?)doc["id"])!.Length);
        Assert.Equal("people", (string?)doc["collection"]);
        Assert.Equal("ada", (string?)doc["fields"]!["name"]!["stringValue"]);
    }

    [Fact]
    public async Task Add_WithoutCreateRule_PermissionDenied()
    {
        await using var host = await WsTestHost.StartAsync(Fx.ConnectionString); // deny-all
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server);

        var resp = await ws.RequestAsync(new JsonObject
        {
            ["type"] = "add",
            ["collection"] = "people",
            ["fields"] = new JsonObject { ["name"] = new JsonObject { ["stringValue"] = "ada" } },
        });

        Assert.Equal("PERMISSION_DENIED", (string?)resp["status"]);
    }
}
