using System.Text.Json.Nodes;

namespace Winche.Database.IntegrationTests.Ws;

[Collection("postgres")]
public class WsOperationTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private Task<WsTestHost> Host() => WsTestHost.StartAsync(Fx.ConnectionString,
        c => c.AddDocumentAccessRule<AllowAllWritesRule>());

    private static JsonObject SetWrite(string path, string field, long value) => new()
    {
        ["type"] = "write",
        ["writes"] = new JsonArray(new JsonObject
        {
            ["set"] = new JsonObject
            {
                ["path"] = path,
                ["fields"] = new JsonObject { [field] = new JsonObject { ["integerValue"] = value.ToString() } },
            },
        }),
    };

    [Fact]
    public async Task Write_Get_Query_Aggregate_RoundTrip()
    {
        await using var host = await Host();
        await using var ws = await WsTestClient.ConnectV3Async(host.Server);

        var write = await ws.RequestAsync(SetWrite("wsops/a", "n", 1));
        Assert.Equal("response", (string?)write["type"]);
        Assert.NotNull(write["result"]!["writeResults"]![0]!["updateTime"]);

        await ws.RequestAsync(SetWrite("wsops/b", "n", 5));

        var get = await ws.RequestAsync(new JsonObject { ["type"] = "doc.get", ["path"] = "wsops/a" });
        Assert.Equal("1", (string?)get["result"]!["document"]!["fields"]!["n"]!["integerValue"]);

        var getAll = await ws.RequestAsync(new JsonObject
            { ["type"] = "doc.getAll", ["paths"] = new JsonArray("wsops/b", "wsops/none") });
        Assert.Equal("5", (string?)getAll["result"]!["documents"]![0]!["fields"]!["n"]!["integerValue"]);
        Assert.Null(getAll["result"]!["documents"]![1]);

        var query = await ws.RequestAsync(new JsonObject
        {
            ["type"] = "query",
            ["query"] = new JsonObject
            {
                ["collection"] = "wsops",
                ["where"] = new JsonObject
                {
                    ["field"] = "n", ["op"] = "gte",
                    ["value"] = new JsonObject { ["integerValue"] = "5" },
                },
            },
        });
        Assert.Single(query["result"]!["documents"]!.AsArray());

        var agg = await ws.RequestAsync(new JsonObject
        {
            ["type"] = "aggregate",
            ["pipeline"] = new JsonObject
            {
                ["pipeline"] = new JsonArray(
                    new JsonObject { ["match"] = new JsonObject { ["collection"] = "wsops" } },
                    new JsonObject
                    {
                        ["group"] = new JsonObject
                        {
                            ["keys"] = new JsonArray(),
                            ["accumulators"] = new JsonArray(new JsonObject { ["as"] = "n", ["fn"] = "count" }),
                        },
                    }),
            },
        });
        Assert.Equal("2", (string?)agg["result"]!["rows"]![0]!["n"]!["integerValue"]);
    }

    [Fact]
    public async Task ErrorStatuses_OnTheWire()
    {
        await using var host = await Host();
        await using var ws = await WsTestClient.ConnectV3Async(host.Server);

        var update = await ws.RequestAsync(new JsonObject
        {
            ["type"] = "write",
            ["writes"] = new JsonArray(new JsonObject
            {
                ["update"] = new JsonObject
                {
                    ["path"] = "wsops/missing",
                    ["fields"] = new JsonObject { ["x"] = new JsonObject { ["integerValue"] = "1" } },
                },
            }),
        });
        Assert.Equal(("error", "NOT_FOUND"), ((string?)update["type"], (string?)update["status"]));

        var badQuery = await ws.RequestAsync(new JsonObject
        {
            ["type"] = "query",
            ["query"] = new JsonObject
            {
                ["collection"] = "c",
                ["where"] = new JsonObject
                    { ["field"] = "f", ["op"] = "bogus", ["value"] = new JsonObject { ["nullValue"] = null } },
            },
        });
        Assert.Equal("INVALID_QUERY", (string?)badQuery["status"]);   // QueryParseException inside JsonException → INVALID_QUERY

        var badWrite = await ws.RequestAsync(new JsonObject
            { ["type"] = "write", ["writes"] = new JsonArray(new JsonObject()) });
        Assert.Equal("INVALID_ARGUMENT", (string?)badWrite["status"]);
    }

    [Fact]
    public async Task SetMerge_NestedSentinel_WS_EndToEnd()
    {
        await using var host = await Host();
        await using var ws = await WsTestClient.ConnectV3Async(host.Server);

        // Set initial document with nested map
        await ws.RequestAsync(new JsonObject
        {
            ["type"] = "write",
            ["writes"] = new JsonArray(new JsonObject
            {
                ["set"] = new JsonObject
                {
                    ["path"] = "wsops/nested",
                    ["fields"] = new JsonObject
                    {
                        ["m"] = new JsonObject
                        {
                            ["mapValue"] = new JsonObject
                            {
                                ["fields"] = new JsonObject
                                {
                                    ["drop"] = new JsonObject { ["integerValue"] = "1" },
                                    ["keep"] = new JsonObject { ["integerValue"] = "2" },
                                },
                            },
                        },
                        ["top"] = new JsonObject { ["integerValue"] = "3" },
                    },
                },
            }),
        });

        // Merge-set with a nested deleteField sentinel
        var mergeResult = await ws.RequestAsync(new JsonObject
        {
            ["type"] = "write",
            ["writes"] = new JsonArray(new JsonObject
            {
                ["set"] = new JsonObject
                {
                    ["path"] = "wsops/nested",
                    ["merge"] = true,
                    ["fields"] = new JsonObject
                    {
                        ["m"] = new JsonObject
                        {
                            ["mapValue"] = new JsonObject
                            {
                                ["fields"] = new JsonObject
                                {
                                    ["drop"] = new JsonObject { ["deleteField"] = true },
                                    ["added"] = new JsonObject { ["integerValue"] = "4" },
                                },
                            },
                        },
                    },
                },
            }),
        });
        Assert.Equal("response", (string?)mergeResult["type"]);

        // Verify the nested key was deleted and siblings preserved
        var get = await ws.RequestAsync(new JsonObject { ["type"] = "doc.get", ["path"] = "wsops/nested" });
        var fields = get["result"]!["document"]!["fields"]!;
        var mFields = fields["m"]!["mapValue"]!["fields"]!;
        Assert.Null(mFields["drop"]);
        Assert.Equal("2", (string?)mFields["keep"]!["integerValue"]);
        Assert.Equal("4", (string?)mFields["added"]!["integerValue"]);
        Assert.Equal("3", (string?)fields["top"]!["integerValue"]);
    }
}
