using System.Text.Json.Nodes;
using Winche.Database.Values;

namespace Winche.Database.Tests.Values;

public class ValueSerializerWriteTests
{
    private static string Json(Value v) => ValueSerializer.Write(v).ToJsonString();

    [Fact] public void Null_Writes() => Assert.Equal("""{"nullValue":null}""", Json(new NullValue()));
    [Fact] public void Boolean_Writes() => Assert.Equal("""{"booleanValue":true}""", Json(new BooleanValue(true)));
    [Fact] public void Integer_WritesAsString() => Assert.Equal("""{"integerValue":"42"}""", Json(new IntegerValue(42)));
    [Fact] public void Integer_Int64Limits() => Assert.Equal($$"""{"integerValue":"{{long.MinValue}}"}""", Json(new IntegerValue(long.MinValue)));
    [Fact] public void Double_WritesAsNumber() => Assert.Equal("""{"doubleValue":1.5}""", Json(new DoubleValue(1.5)));
    [Fact] public void Double_NaN_WritesAsString() => Assert.Equal("""{"doubleValue":"NaN"}""", Json(new DoubleValue(double.NaN)));
    [Fact] public void Double_Infinity_WritesAsString() => Assert.Equal("""{"doubleValue":"Infinity"}""", Json(new DoubleValue(double.PositiveInfinity)));
    [Fact] public void Double_NegInfinity_WritesAsString() => Assert.Equal("""{"doubleValue":"-Infinity"}""", Json(new DoubleValue(double.NegativeInfinity)));
    // System.Text.Json escapes non-ASCII by default; compare via DeepEquals to avoid encoding mismatch.
    [Fact]
    public void String_Writes()
    {
        var expected = JsonNode.Parse("""{"stringValue":"héllo 🌍"}""");
        var actual   = ValueSerializer.Write(new StringValue("héllo 🌍"));
        Assert.True(JsonNode.DeepEquals(expected, actual));
    }

    [Fact]
    public void Timestamp_WritesUtcRfc3339()
    {
        var ts = new DateTimeOffset(2026, 6, 6, 14, 30, 0, TimeSpan.FromHours(2)).AddTicks(1234567);
        Assert.Equal("""{"timestampValue":"2026-06-06T12:30:00.123456Z"}""", Json(new TimestampValue(ts)));
    }

    [Fact] public void Bytes_WritesBase64() => Assert.Equal("""{"bytesValue":"AQID"}""", Json(new BytesValue([1, 2, 3])));
    [Fact] public void Reference_Writes() => Assert.Equal("""{"referenceValue":"users/u1"}""", Json(new ReferenceValue("users/u1")));
    [Fact] public void GeoPoint_Writes() => Assert.Equal("""{"geoPointValue":{"latitude":59.9,"longitude":10.7}}""", Json(new GeoPointValue(59.9, 10.7)));

    [Fact]
    public void Array_Writes() =>
        Assert.Equal("""{"arrayValue":{"values":[{"integerValue":"1"},{"stringValue":"x"}]}}""",
            Json(new ArrayValue([new IntegerValue(1), new StringValue("x")])));

    [Fact] public void EmptyArray_WritesExplicitValues() => Assert.Equal("""{"arrayValue":{"values":[]}}""", Json(new ArrayValue([])));

    [Fact]
    public void Map_Writes() =>
        Assert.Equal("""{"mapValue":{"fields":{"a":{"booleanValue":false}}}}""",
            Json(new MapValue(new Dictionary<string, Value> { ["a"] = new BooleanValue(false) })));

    [Fact] public void EmptyMap_WritesExplicitFields() => Assert.Equal("""{"mapValue":{"fields":{}}}""", Json(new MapValue(new Dictionary<string, Value>())));
}
