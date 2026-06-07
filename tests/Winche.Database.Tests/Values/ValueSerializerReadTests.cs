using System.Text.Json.Nodes;
using Winche.Database.Values;

namespace Winche.Database.Tests.Values;

public class ValueSerializerReadTests
{
    private static Value Read(string json) => ValueSerializer.Read(JsonNode.Parse(json)!);

    public static TheoryData<Value> RoundTripValues => new()
    {
        new NullValue(),
        new BooleanValue(true),
        new BooleanValue(false),
        new IntegerValue(0),
        new IntegerValue(long.MaxValue),
        new IntegerValue(long.MinValue),
        new DoubleValue(1.5),
        new DoubleValue(double.NaN),
        new DoubleValue(double.PositiveInfinity),
        new DoubleValue(double.NegativeInfinity),
        new TimestampValue(new DateTimeOffset(2026, 6, 6, 12, 30, 0, TimeSpan.Zero)),
        new StringValue(""),
        new StringValue("héllo 🌍"),
        new BytesValue([]),
        new BytesValue([1, 2, 3]),
        new ReferenceValue("users/u1/orders/o1"),
        new GeoPointValue(-90, 180),
        new ArrayValue([]),
        new ArrayValue([new IntegerValue(1), new ArrayValue([new StringValue("nested")])]),
        new MapValue(new Dictionary<string, Value>()),
        new MapValue(new Dictionary<string, Value>
        {
            ["deep"] = new MapValue(new Dictionary<string, Value> { ["x"] = new TimestampValue(DateTimeOffset.UnixEpoch) }),
        }),
    };

    [Theory]
    [MemberData(nameof(RoundTripValues))]
    public void RoundTrip_PreservesValue(Value original)
    {
        var reparsed = ValueSerializer.Read(JsonNode.Parse(ValueSerializer.Write(original).ToJsonString())!);
        Assert.Equal(original, reparsed);
    }

    [Fact]
    public void Integer_AcceptsJsonNumberToo()
    {
        Assert.Equal(new IntegerValue(42), Read("""{"integerValue":42}"""));
    }

    [Fact]
    public void UnknownTag_Throws()
    {
        var ex = Assert.Throws<WireFormatException>(() => Read("""{"bogusValue":1}"""));
        Assert.Contains("bogusValue", ex.Message);
    }

    [Fact]
    public void MultipleTags_Throws()
    {
        Assert.Throws<WireFormatException>(() => Read("""{"integerValue":"1","stringValue":"x"}"""));
    }

    [Fact]
    public void NonObject_Throws()
    {
        Assert.Throws<WireFormatException>(() => Read("\"plain string\""));
        Assert.Throws<WireFormatException>(() => Read("42"));
    }

    [Fact]
    public void BadTimestamp_Throws()
    {
        Assert.Throws<WireFormatException>(() => Read("""{"timestampValue":"not-a-date"}"""));
    }

    [Fact]
    public void BadBase64_Throws()
    {
        Assert.Throws<WireFormatException>(() => Read("""{"bytesValue":"!!!not base64!!!"}"""));
    }

    [Fact]
    public void NullValue_NonNullPayload_Throws()
    {
        Assert.Throws<WireFormatException>(() => Read("""{"nullValue":1}"""));
        Assert.Throws<WireFormatException>(() => Read("""{"nullValue":"NULL_VALUE"}"""));
        Assert.Equal(new NullValue(), Read("""{"nullValue":null}"""));
    }
}
