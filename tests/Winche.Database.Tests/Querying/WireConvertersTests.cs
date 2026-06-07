// tests/Winche.Database.Tests/Querying/WireConvertersTests.cs
using System.Text.Json;
using System.Text.Json.Nodes;
using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Values;

namespace Winche.Database.Tests.Querying;

public class WireConvertersTests
{
    [Fact]
    public void Value_RoundTripsViaDefaultSerializer()
    {
        var v = new MapValue(new Dictionary<string, Value> { ["a"] = new IntegerValue(5) });
        var json = JsonSerializer.Serialize<Value>(v);
        Assert.Equal("""{"mapValue":{"fields":{"a":{"integerValue":"5"}}}}""", json);
        Assert.Equal(v, JsonSerializer.Deserialize<Value>(json));
    }

    [Fact]
    public void Value_BadWire_ThrowsJsonException()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Value>("""{"bogusValue":1}"""));
    }

    [Fact]
    public void Document_SerializesWireShape()
    {
        var doc = new Document
        {
            Path = "users/u1", Id = "u1", Collection = "users",
            Fields = new Dictionary<string, Value> { ["age"] = new IntegerValue(30) },
            CreateTime = new DateTimeOffset(2026, 6, 6, 0, 0, 0, TimeSpan.Zero),
            UpdateTime = new DateTimeOffset(2026, 6, 6, 1, 0, 0, TimeSpan.Zero),
            Version = 2,
        };
        var node = JsonNode.Parse(JsonSerializer.Serialize(doc))!;
        Assert.Equal("users/u1", (string)node["path"]!);
        Assert.Equal("u1", (string)node["id"]!);
        Assert.Equal("users", (string)node["collection"]!);
        Assert.Equal(2, (long)node["version"]!);
        Assert.Equal("30", (string)node["fields"]!["age"]!["integerValue"]!);
        Assert.StartsWith("2026-06-06T00:00:00", (string)node["createTime"]!);
    }

    [Fact]
    public void QueryAst_DeserializesViaQueryParser()
    {
        var q = JsonSerializer.Deserialize<QueryAst>(
            """{"collection":"c","where":{"field":"age","op":"gt","value":{"integerValue":"21"}},"limit":5}""")!;
        Assert.Equal("c", q.Collection);
        Assert.Equal(5, q.Limit);
        Assert.IsType<FieldFilterAst>(q.Where);
    }

    [Fact]
    public void QueryAst_RoundTripsCanonically()
    {
        var json = """{"collection":"c","where":{"and":[{"field":"a","op":"eq","value":{"booleanValue":true}},{"unary":"exists","field":"b"}]},"orderBy":[{"field":"a","direction":"desc"}],"limit":7,"start":{"values":[{"integerValue":"1"}],"before":true}}""";
        var q = JsonSerializer.Deserialize<QueryAst>(json)!;
        var q2 = JsonSerializer.Deserialize<QueryAst>(JsonSerializer.Serialize(q))!;
        Assert.Equal(q, q2);
    }

    [Fact]
    public void PipelineAst_RoundTrips()
    {
        var json = """{"pipeline":[{"match":{"collection":"c"}},{"group":{"keys":[{"as":"k","field":"x"}],"accumulators":[{"as":"n","fn":"count"}]}},{"limit":3}]}""";
        var p = JsonSerializer.Deserialize<PipelineAst>(json)!;
        Assert.Equal(3, p.Stages.Count);
        var p2 = JsonSerializer.Deserialize<PipelineAst>(JsonSerializer.Serialize(p))!;
        Assert.Equal(p.Stages.Count, p2.Stages.Count);
        Assert.Equal(p.Stages[1], p2.Stages[1]);
    }

    [Fact]
    public void QueryAst_BadWire_ThrowsJsonExceptionWithPath()
    {
        var ex = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<QueryAst>("""{"collection":"c","where":{"field":"f","op":"bogus","value":{"nullValue":null}}}"""));
        Assert.Contains("$.where.op", ex.Message);
    }
}
