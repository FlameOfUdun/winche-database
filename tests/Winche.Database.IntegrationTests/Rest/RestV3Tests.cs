using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Winche.Database.IntegrationTests.Rest;

[Collection("postgres")]
public class RestV3Tests(PostgresFixture fx) : QueryTestBase(fx)
{
    private Task<RestTestHost> Host() => RestTestHost.StartAsync(Fx.ConnectionString,
        c => c.AddDocumentAccessRule<RestAllowAllRule>());

    /// <summary>POST a JSON string body; returns (status, parsed body).</summary>
    private static async Task<(HttpStatusCode Status, JsonObject Body)> PostAsync(
        HttpClient client, string route, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PostAsync(route, content);
        var text = await response.Content.ReadAsStringAsync();
        var body = (JsonObject?)(text.Length > 0 ? JsonNode.Parse(text) : null) ?? new JsonObject();
        return (response.StatusCode, body);
    }

    // Not used currently — kept for reference
    // private static string SetWriteJson(...)

    // Case 1: :commit with a set + a set with increment transform; verify writeResults and GET
    [Fact]
    public async Task Commit_AtomicBatch_WithTransform()
    {
        await using var host = await Host();
        var client = host.Client;
        const string path = "rest1/t1";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(path));

        var json =
            """
            {
              "writes": [
                {"set": {"path": "rest1/t1", "fields": {"n": {"integerValue": "1"}}}},
                {"set": {"path": "rest1/t1", "fields": {"n": {"integerValue": "1"}},
                  "transforms": [{"field": "n", "kind": "increment", "operand": {"integerValue": "2"}}]}}
              ]
            }
            """;

        var (status, body) = await PostAsync(client, "/documents:commit", json);

        Assert.Equal(HttpStatusCode.OK, status);
        var writeResults = body["writeResults"]!.AsArray();
        Assert.Equal(2, writeResults.Count);
        // second write has the increment transform result
        Assert.NotNull(writeResults[1]!["transformResults"]);
        Assert.NotNull(writeResults[1]!["updateTime"]);

        // GET the document and verify final state
        var getResp = await client.GetAsync($"/documents/{b64}");
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var doc = (JsonObject?)JsonNode.Parse(await getResp.Content.ReadAsStringAsync()) ?? new JsonObject();
        Assert.NotNull(doc["fields"]!["n"]!["integerValue"]);
    }

    // Case 2: :commit with update on missing doc → 404, status == "NOT_FOUND"
    [Fact]
    public async Task Commit_UpdateMissing_404_NOT_FOUND()
    {
        await using var host = await Host();
        var client = host.Client;

        var (status, body) = await PostAsync(client, "/documents:commit",
            """{"writes": [{"update": {"path": "rest2/missing", "fields": {"x": {"integerValue": "1"}}}}]}""");

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("NOT_FOUND", (string?)body["status"]);
    }

    // Case 3: Tx read-commit happy path AND conflict 409
    [Fact]
    public async Task Tx_ReadCommit_Happy_And_Conflict409()
    {
        await using var host = await Host();
        var client = host.Client;
        const string path = "rest3/tx";

        // seed
        await PostAsync(client, "/documents:commit",
            """{"writes": [{"set": {"path": "rest3/tx", "fields": {"n": {"integerValue": "0"}}}}]}""");

        // --- happy path ---
        var (beginStatus, beginBody) = await PostAsync(client, "/documents:beginTransaction", "{}");
        Assert.Equal(HttpStatusCode.OK, beginStatus);
        var txId = (string?)beginBody["transaction"];
        Assert.NotNull(txId);

        var batchJson = $@"{{""documents"": [""{path}""], ""transaction"": ""{txId}""}}";
        var (batchStatus, _) = await PostAsync(client, "/documents:batchGet", batchJson);
        Assert.Equal(HttpStatusCode.OK, batchStatus);

        var commitJson = $@"{{""writes"": [{{""set"": {{""path"": ""{path}"", ""fields"": {{""n"": {{""integerValue"": ""1""}}}}}}}}], ""transaction"": ""{txId}""}}";
        var (commitStatus, commitBody) = await PostAsync(client, "/documents:commit", commitJson);
        Assert.Equal(HttpStatusCode.OK, commitStatus);
        Assert.NotNull(commitBody["writeResults"]);

        // --- conflict path ---
        var (beginStatus2, beginBody2) = await PostAsync(client, "/documents:beginTransaction", "{}");
        var txId2 = (string?)beginBody2["transaction"];
        Assert.NotNull(txId2);

        // record read set
        var batchJson2 = $@"{{""documents"": [""{path}""], ""transaction"": ""{txId2}""}}";
        await PostAsync(client, "/documents:batchGet", batchJson2);

        // interloper plain :commit
        await PostAsync(client, "/documents:commit",
            """{"writes": [{"set": {"path": "rest3/tx", "fields": {"n": {"integerValue": "99"}}}}]}""");

        // tx :commit → ABORTED → 409
        var commitJson2 = $@"{{""writes"": [{{""set"": {{""path"": ""{path}"", ""fields"": {{""n"": {{""integerValue"": ""2""}}}}}}}}], ""transaction"": ""{txId2}""}}";

        var (abortedStatus, abortedBody) = await PostAsync(client, "/documents:commit", commitJson2);
        Assert.Equal(HttpStatusCode.Conflict, abortedStatus);
        Assert.Equal("ABORTED", (string?)abortedBody["status"]);
    }

    // Case 4: :rollback with unknown id → 200 {}
    [Fact]
    public async Task Rollback_UnknownId_IsNoOp200()
    {
        await using var host = await Host();
        var client = host.Client;

        var (status, body) = await PostAsync(client, "/documents:rollback",
            """{"transaction": "00000000000000000000000000000000"}""");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Empty(body);
    }

    // Case 5: :batchGet preserves order with JSON nulls for missing docs
    [Fact]
    public async Task BatchGet_OrderAndNulls()
    {
        await using var host = await Host();
        var client = host.Client;

        // seed
        await PostAsync(client, "/documents:commit",
            """{"writes": [{"set": {"path": "rest5/exists", "fields": {"v": {"integerValue": "42"}}}}]}""");

        var (status, body) = await PostAsync(client, "/documents:batchGet",
            """{"documents": ["rest5/exists", "rest5/missing", "rest5/exists"]}""");

        Assert.Equal(HttpStatusCode.OK, status);
        var docs = body["documents"]!.AsArray();
        Assert.Equal(3, docs.Count);
        Assert.NotNull(docs[0]);
        Assert.Equal("42", (string?)docs[0]!["fields"]!["v"]!["integerValue"]);
        Assert.Null(docs[1]);
        Assert.NotNull(docs[2]);
    }

    // Case 6: :runQuery with filter and :aggregate count pipeline
    [Fact]
    public async Task RunQuery_Filter_And_Aggregate()
    {
        await using var host = await Host();
        var client = host.Client;

        // seed collection rest6
        await PostAsync(client, "/documents:commit",
            """
            {"writes": [
              {"set": {"path": "rest6/a", "fields": {"n": {"integerValue": "1"}}}},
              {"set": {"path": "rest6/b", "fields": {"n": {"integerValue": "5"}}}},
              {"set": {"path": "rest6/c", "fields": {"n": {"integerValue": "10"}}}}
            ]}
            """);

        // :runQuery — filter n >= 5 → b, c
        var (queryStatus, queryBody) = await PostAsync(client, "/documents:runQuery",
            """{"query": {"collection": "rest6", "where": {"field": "n", "op": "gte", "value": {"integerValue": "5"}}}}""");

        Assert.Equal(HttpStatusCode.OK, queryStatus);
        var queryDocs = queryBody["documents"]!.AsArray();
        Assert.Equal(2, queryDocs.Count);

        // :aggregate — count all rest6 docs → 3
        var (aggStatus, aggBody) = await PostAsync(client, "/documents:aggregate",
            """
            {"pipeline": {"pipeline": [
              {"match": {"collection": "rest6"}},
              {"group": {"keys": [], "accumulators": [{"as": "total", "fn": "count"}]}}
            ]}}
            """);

        Assert.Equal(HttpStatusCode.OK, aggStatus);
        var rows = aggBody["rows"]!.AsArray();
        Assert.Single(rows);
        var totalStr = (string?)rows[0]!["total"]!["integerValue"];
        Assert.Equal(3L, long.Parse(totalStr!));
    }

    // Case 7: Old routes gone — POST /documents/query is no longer a registered POST endpoint.
    // ASP.NET returns 405 (MethodNotAllowed) because the /{path} GET route still matches the
    // path segment; either 404 or 405 proves there is no POST /query handler.
    [Fact]
    public async Task OldRoutes_Gone()
    {
        await using var host = await Host();
        var client = host.Client;

        var resp = await client.PostAsync("/documents/query",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        // 404 or 405 both confirm the POST /documents/query endpoint was removed
        Assert.True(
            resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
            $"Expected 404 or 405 but got {(int)resp.StatusCode}");
    }

    // Case 8: :commit with invalid write object → 400, status == "INVALID_ARGUMENT"
    [Fact]
    public async Task InvalidWrite_400_INVALID_ARGUMENT()
    {
        await using var host = await Host();
        var client = host.Client;

        var (status, body) = await PostAsync(client, "/documents:commit",
            """{"writes": [{}]}""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("INVALID_ARGUMENT", (string?)body["status"]);
    }

    // C2 regression (a): :rollback with malformed JSON → 400 INVALID_ARGUMENT
    [Fact]
    public async Task Rollback_MalformedJson_400_INVALID_ARGUMENT()
    {
        await using var host = await Host();
        var client = host.Client;

        var (status, body) = await PostAsync(client, "/documents:rollback", "{not json");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("INVALID_ARGUMENT", (string?)body["status"]);
    }

    // C2 regression (b): :rollback with valid JSON but missing 'transaction' field → 400 INVALID_ARGUMENT
    [Fact]
    public async Task Rollback_MissingTransaction_400_INVALID_ARGUMENT()
    {
        await using var host = await Host();
        var client = host.Client;

        var (status, body) = await PostAsync(client, "/documents:rollback", "{}");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("INVALID_ARGUMENT", (string?)body["status"]);
    }

    // C3 regression (a): :runQuery with invalid 'op' → 400 INVALID_QUERY with details.jsonPath non-null
    [Fact]
    public async Task RunQuery_BadOp_400_INVALID_QUERY()
    {
        await using var host = await Host();
        var client = host.Client;

        var (status, body) = await PostAsync(client, "/documents:runQuery",
            """{"query":{"collection":"c","where":{"field":"f","op":"bogus","value":{"nullValue":null}}}}""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("INVALID_QUERY", (string?)body["status"]);
        Assert.NotNull(body["details"]?["jsonPath"]);
    }
}
