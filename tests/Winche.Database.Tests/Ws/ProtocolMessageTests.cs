using System.Text.Json;
using System.Text.Json.Nodes;
using Winche.Database.AspNetCore.WebSockets.Protocol;
using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Runtime.Listening;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.Tests.Ws;

public class ProtocolMessageTests
{
    private static ClientMessage In(string json) => JsonSerializer.Deserialize<ClientMessage>(json)!;
    private static JsonNode Out(ServerMessage msg) => JsonNode.Parse(JsonSerializer.Serialize(msg, msg.GetType()))!;

    [Fact]
    public void Ping_Deserializes()
    {
        Assert.IsType<PingMessage>(In("""{"type":"ping","id":"1"}"""));
    }

    [Fact]
    public void Operations_Deserialize()
    {
        Assert.Equal("c/a", Assert.IsType<DocGetMessage>(In("""{"type":"doc.get","id":"1","path":"c/a"}""")).Path);
        Assert.Equal(2, Assert.IsType<DocGetAllMessage>(In("""{"type":"doc.getAll","id":"1","paths":["a/b","a/c"]}""")).Paths.Count);
        Assert.Equal("c", Assert.IsType<QueryMessage>(In("""{"type":"query","id":"1","query":{"collection":"c"}}""")).Query.Collection);
        Assert.Single(Assert.IsType<WriteMessage>(In("""{"type":"write","id":"1","writes":[{"delete":{"path":"c/a"}}]}""")).Writes);
        Assert.IsType<TxBeginMessage>(In("""{"type":"tx.begin","id":"1"}"""));
        var commit = Assert.IsType<TxCommitMessage>(In("""{"type":"tx.commit","id":"1","transactionId":"t","writes":[]}"""));
        Assert.Equal("t", commit.TransactionId);
        var listen = Assert.IsType<ListenMessage>(In("""{"type":"listen","id":"1","query":{"collection":"c"},"resumeToken":42}"""));
        Assert.Equal(42, listen.ResumeToken);
        Assert.Equal("s1", Assert.IsType<UnlistenMessage>(In("""{"type":"unlisten","id":"1","subscriptionId":"s1"}""")).SubscriptionId);
    }

    [Fact]
    public void UnknownType_ThrowsJsonException() =>
        Assert.ThrowsAny<JsonException>(() => In("""{"type":"bogus","id":"1"}"""));

    [Fact]
    public void ServerMessages_SerializeWireShapes()
    {
        var welcome = Out(new WelcomeMessage { ConnectionId = "c1" });
        Assert.Equal("welcome", (string)welcome["type"]!);
        Assert.Equal("c1", (string)welcome["connectionId"]!);

        var response = Out(new ResponseMessage { Id = "1", Result = new JsonObject { ["x"] = 1 } });
        Assert.Equal("response", (string)response["type"]!);
        Assert.Equal(1, (int)response["result"]!["x"]!);

        var error = Out(new ErrorMessage { Id = "1", Status = "NOT_FOUND", Message = "m" });
        Assert.Equal(("error", "NOT_FOUND"), ((string)error["type"]!, (string)error["status"]!));

        var doc = new Document
        {
            Path = "c/a", Id = "a", Collection = "c",
            Fields = new Dictionary<string, Value> { ["x"] = new IntegerValue(1) },
            CreateTime = DateTimeOffset.UnixEpoch, UpdateTime = DateTimeOffset.UnixEpoch, Version = 1,
        };
        var snap = Out(new ListenSnapshotMessage
            { SubscriptionId = "s1", Documents = [doc], ReadTime = DateTimeOffset.UnixEpoch, ResumeToken = 7 });
        Assert.Equal("listen.snapshot", (string)snap["type"]!);
        Assert.Equal("1", (string)snap["documents"]![0]!["fields"]!["x"]!["integerValue"]!);
        Assert.Equal(7, (long)snap["resumeToken"]!);

        var delta = Out(new ListenDeltaMessage
        {
            SubscriptionId = "s1",
            Changes = [new WireChange("added", doc, -1, 0)],
            Count = 1, ReadTime = DateTimeOffset.UnixEpoch, ResumeToken = 8,
        });
        Assert.Equal("listen.delta", (string)delta["type"]!);
        Assert.Equal(("added", -1, 0),
            ((string)delta["changes"]![0]!["kind"]!, (int)delta["changes"]![0]!["oldIndex"]!, (int)delta["changes"]![0]!["newIndex"]!));
        Assert.Equal(1, (int)delta["count"]!);
    }

    [Fact]
    public void WriteResult_SerializesWireShape()
    {
        // No transforms: only updateTime
        var simple = new WriteResult(DateTimeOffset.UnixEpoch);
        var simpleNode = JsonNode.Parse(JsonSerializer.Serialize(simple, simple.GetType()))!;
        Assert.Equal("1970-01-01T00:00:00+00:00", (string)simpleNode["updateTime"]!);
        Assert.Null(simpleNode["transformResults"]);

        // With transforms: keys are dotted field-path strings; values are Value wire
        var transforms = new Dictionary<FieldPath, Value>
        {
            [FieldPath.Parse("a.b")] = new IntegerValue(42),
            [FieldPath.Parse("ts")] = new TimestampValue(DateTimeOffset.UnixEpoch),
        };
        var withTransforms = new WriteResult(DateTimeOffset.UnixEpoch, transforms);
        var node = JsonNode.Parse(JsonSerializer.Serialize(withTransforms, withTransforms.GetType()))!;
        Assert.Equal("42", (string)node["transformResults"]!["a.b"]!["integerValue"]!);
        Assert.NotNull(node["transformResults"]!["ts"]!["timestampValue"]);
    }
}
