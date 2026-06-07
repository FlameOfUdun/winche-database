using Winche.Database.Documents;
using Winche.Database.Values;

namespace Winche.Database.Tests.Documents;

public class StorageCodecTests
{
    [Fact]
    public void Encode_ProducesTaggedFieldMap()
    {
        var fields = new Dictionary<string, Value>
        {
            ["age"] = new IntegerValue(30),
            ["name"] = new StringValue("Ada"),
        };
        Assert.Equal("""{"age":{"integerValue":"30"},"name":{"stringValue":"Ada"}}""", StorageCodec.Encode(fields));
    }

    [Fact]
    public void Encode_EmptyFields_ProducesEmptyObject()
    {
        Assert.Equal("{}", StorageCodec.Encode(new Dictionary<string, Value>()));
    }

    [Fact]
    public void RoundTrip_PreservesAllTypes()
    {
        var fields = new Dictionary<string, Value>
        {
            ["n"] = new NullValue(),
            ["b"] = new BooleanValue(true),
            ["i"] = new IntegerValue(long.MaxValue),
            ["d"] = new DoubleValue(1.5),
            ["nan"] = new DoubleValue(double.NaN),
            ["ts"] = new TimestampValue(new DateTimeOffset(2026, 6, 6, 0, 0, 0, TimeSpan.Zero)),
            ["s"] = new StringValue("héllo"),
            ["by"] = new BytesValue([1, 2, 3]),
            ["ref"] = new ReferenceValue("users/u1"),
            ["geo"] = new GeoPointValue(59.9, 10.7),
            ["arr"] = new ArrayValue([new IntegerValue(1), new StringValue("x")]),
            ["map"] = new MapValue(new Dictionary<string, Value> { ["nested"] = new BooleanValue(false) }),
        };

        var decoded = StorageCodec.Decode(StorageCodec.Encode(fields));

        Assert.Equal(fields.Count, decoded.Count);
        foreach (var (key, value) in fields)
            Assert.Equal(value, decoded[key]);
    }

    [Fact]
    public void Decode_InvalidJson_Throws()
    {
        Assert.Throws<WireFormatException>(() => StorageCodec.Decode("not json"));
    }

    [Fact]
    public void Decode_NonTaggedField_Throws()
    {
        Assert.Throws<WireFormatException>(() => StorageCodec.Decode("""{"age":30}"""));
    }
}
