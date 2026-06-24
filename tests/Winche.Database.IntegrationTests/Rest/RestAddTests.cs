using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Winche.Rules;
using Winche.Rules.Expressions;

namespace Winche.Database.IntegrationTests.Rest;

[Collection("postgres")]
public class RestAddTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private Task<RestTestHost> AllowAllHost() => RestTestHost.StartAsync(Fx.ConnectionString,
        c => c.UseRules(r => r.Match("{document=**}", b => b.Allow(RuleOperations.All, Expr.Const(true)))));

    private Task<RestTestHost> DenyAllHost() => RestTestHost.StartAsync(Fx.ConnectionString); // no rules → deny-all

    private static async Task<(HttpStatusCode Status, JsonObject Body)> PostAsync(HttpClient client, string route, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PostAsync(route, content);
        var text = await response.Content.ReadAsStringAsync();
        var body = (JsonObject?)(text.Length > 0 ? JsonNode.Parse(text) : null) ?? new JsonObject();
        return (response.StatusCode, body);
    }

    [Fact]
    public async Task Add_CreatesDocument_WithGeneratedId()
    {
        await using var host = await AllowAllHost();
        var (status, body) = await PostAsync(host.Client, "/documents:add",
            """{"collection": "people", "fields": {"name": {"stringValue": "ada"}}}""");

        Assert.Equal(HttpStatusCode.OK, status);
        var doc = body["document"]!.AsObject();
        Assert.Equal(20, ((string?)doc["id"])!.Length);
        Assert.Equal("people", (string?)doc["collection"]);
        Assert.Equal("ada", (string?)doc["fields"]!["name"]!["stringValue"]);
    }

    [Fact]
    public async Task Add_NonStringCollection_ReturnsBadRequest()
    {
        await using var host = await AllowAllHost();
        var (status, body) = await PostAsync(host.Client, "/documents:add",
            """{"collection": 42, "fields": {"name": {"stringValue": "ada"}}}""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("INVALID_ARGUMENT", (string?)body["status"]);
    }

    [Fact]
    public async Task Add_WithoutCreateRule_PermissionDenied()
    {
        await using var host = await DenyAllHost();
        var (status, body) = await PostAsync(host.Client, "/documents:add",
            """{"collection": "people", "fields": {"name": {"stringValue": "ada"}}}""");

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("PERMISSION_DENIED", (string?)body["status"]);
    }
}
