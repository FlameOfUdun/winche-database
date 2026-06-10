using System.Text.Json;
using System.Text.Json.Nodes;
using Winche.Database.Documents;
using Winche.Database.Querying;
using Winche.Database.Values;

namespace Winche.Database.Tests.Querying;

public class WireSmokeTests
{
    private static Document Doc() => new()
    {
        Path = "c/a", Id = "a", Collection = "c",
        Fields = new Dictionary<string, Value> { ["x"] = new IntegerValue(1) },
        CreateTime = DateTimeOffset.UnixEpoch, UpdateTime = DateTimeOffset.UnixEpoch, Version = 1,
    };

    [Fact]
    public void QueryResult_DefaultOptions_CamelCaseWire()
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(new QueryResult([Doc()], true)))!;
        Assert.NotNull(node["documents"]);
        Assert.True((bool)node["hasMore"]!);
        Assert.Equal("c/a", (string)node["documents"]![0]!["path"]!);
    }

    // ClientMessages_Deserialize was removed: WS project is unloaded from the solution
    // (its refactor is a separate cycle). WireSmokeTests for WS wire format move to the WS project.
}
