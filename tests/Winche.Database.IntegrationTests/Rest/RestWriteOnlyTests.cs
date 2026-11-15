using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Winche.Database.IntegrationTests.Rest;

/// <summary>A write the rules allow but whose result the caller can't read: it's saved, and answered without the document.</summary>
[Collection("postgres")]
public class RestWriteOnlyTests(PostgresFixture fx) : QueryTestBase(fx)
{
    [Fact]
    public async Task A_write_you_cannot_read_back_is_saved_and_answered_204()
    {
        await using var host = await RestTestHost.StartAsync(Fx.ConnectionString, c => c.UseRulesText("""
            match drop/{id} {
              allow write: if true;
              allow read: if resource.data.open == true;
            }
            """));
        var path = System.Buffers.Text.Base64Url.EncodeToString("drop/d1"u8);
        StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

        // Saved, but not readable by the writer: 204, no document.
        using var put = await host.Client.PutAsync($"/documents/v1/{path}", Json("""{ "fields": { "open": { "booleanValue": false } } }"""));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal("", await put.Content.ReadAsStringAsync());

        // It was saved: opening it (a PATCH of the existing document) answers with it.
        using var patch = await host.Client.PatchAsync($"/documents/v1/{path}", Json("""{ "fields": { "open": { "booleanValue": true } } }"""));
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var doc = JsonNode.Parse(await patch.Content.ReadAsStringAsync())!;
        Assert.True(doc["fields"]!["open"]!["booleanValue"]!.GetValue<bool>());
    }
}
