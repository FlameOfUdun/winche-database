using Winche.Database.Authorization;
using Winche.Database.Values;
using Winche.Rules;

namespace Winche.Database.Tests.Authorization;

public class ValueToRuleValueTests
{
    [Fact]
    public void NullValue_MapsToRuleNull()
    {
        var result = ValueToRuleValue.Convert(new NullValue());
        Assert.Equal(RuleValueKind.Null, result.Kind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BooleanValue_MapsToRuleBool(bool v)
    {
        var result = ValueToRuleValue.Convert(new BooleanValue(v));
        Assert.Equal(RuleValueKind.Bool, result.Kind);
        Assert.Equal(v, result.AsBool);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void IntegerValue_MapsToRuleInt(long v)
    {
        var result = ValueToRuleValue.Convert(new IntegerValue(v));
        Assert.Equal(RuleValueKind.Int, result.Kind);
        Assert.Equal(v, result.AsInt);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(3.14)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void DoubleValue_MapsToRuleDouble(double v)
    {
        var result = ValueToRuleValue.Convert(new DoubleValue(v));
        Assert.Equal(RuleValueKind.Double, result.Kind);
        Assert.Equal(v, result.AsDouble);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("unicode: é")]
    public void StringValue_MapsToRuleString(string v)
    {
        var result = ValueToRuleValue.Convert(new StringValue(v));
        Assert.Equal(RuleValueKind.String, result.Kind);
        Assert.Equal(v, result.AsString);
    }

    [Fact]
    public void BytesValue_MapsToRuleBytes()
    {
        byte[] data = [1, 2, 3];
        var result = ValueToRuleValue.Convert(new BytesValue(data));
        Assert.Equal(RuleValueKind.Bytes, result.Kind);
        Assert.Equal(data, result.AsBytes);
    }

    [Fact]
    public void TimestampValue_MapsToRuleTimestamp()
    {
        var ts = new DateTimeOffset(2024, 1, 15, 10, 30, 0, TimeSpan.Zero);
        var result = ValueToRuleValue.Convert(new TimestampValue(ts));
        Assert.Equal(RuleValueKind.Timestamp, result.Kind);
        // TimestampValue truncates to microseconds; compare the rule value's timestamp
        Assert.Equal(new TimestampValue(ts).Value, result.AsTimestamp);
    }

    [Fact]
    public void ReferenceValue_MapsToRulePath()
    {
        var result = ValueToRuleValue.Convert(new ReferenceValue("users/u1"));
        Assert.Equal(RuleValueKind.Path, result.Kind);
        Assert.Equal("users/u1", result.AsPath);
    }

    [Fact]
    public void GeoPointValue_MapsToRuleMap_WithLatLon()
    {
        var result = ValueToRuleValue.Convert(new GeoPointValue(51.5, -0.1));
        Assert.Equal(RuleValueKind.Map, result.Kind);
        var map = result.AsMap;
        Assert.True(map.ContainsKey("latitude"));
        Assert.True(map.ContainsKey("longitude"));
        Assert.Equal(51.5, map["latitude"].AsDouble);
        Assert.Equal(-0.1, map["longitude"].AsDouble);
    }

    [Fact]
    public void ArrayValue_MapsToRuleList_Recursive()
    {
        var array = new ArrayValue([new IntegerValue(1), new StringValue("a"), new NullValue()]);
        var result = ValueToRuleValue.Convert(array);
        Assert.Equal(RuleValueKind.List, result.Kind);
        var list = result.AsList;
        Assert.Equal(3, list.Count);
        Assert.Equal(RuleValueKind.Int, list[0].Kind);
        Assert.Equal(1L, list[0].AsInt);
        Assert.Equal(RuleValueKind.String, list[1].Kind);
        Assert.Equal("a", list[1].AsString);
        Assert.Equal(RuleValueKind.Null, list[2].Kind);
    }

    [Fact]
    public void MapValue_MapsToRuleMap_Recursive()
    {
        var map = new MapValue(new Dictionary<string, Value>
        {
            ["x"] = new IntegerValue(42),
            ["y"] = new BooleanValue(true),
        });
        var result = ValueToRuleValue.Convert(map);
        Assert.Equal(RuleValueKind.Map, result.Kind);
        var rmap = result.AsMap;
        Assert.Equal(2, rmap.Count);
        Assert.Equal(42L, rmap["x"].AsInt);
        Assert.True(rmap["y"].AsBool);
    }

    [Fact]
    public void NestedArray_InMap_ConvertsRecursively()
    {
        var value = new MapValue(new Dictionary<string, Value>
        {
            ["tags"] = new ArrayValue([new StringValue("a"), new StringValue("b")]),
        });
        var result = ValueToRuleValue.Convert(value);
        var inner = result.AsMap["tags"];
        Assert.Equal(RuleValueKind.List, inner.Kind);
        Assert.Equal(2, inner.AsList.Count);
        Assert.Equal("a", inner.AsList[0].AsString);
    }

    [Fact]
    public void NestedMap_InArray_ConvertsRecursively()
    {
        var value = new ArrayValue([
            new MapValue(new Dictionary<string, Value> { ["k"] = new IntegerValue(7) })
        ]);
        var result = ValueToRuleValue.Convert(value);
        Assert.Equal(RuleValueKind.List, result.Kind);
        var inner = result.AsList[0];
        Assert.Equal(RuleValueKind.Map, inner.Kind);
        Assert.Equal(7L, inner.AsMap["k"].AsInt);
    }
}
