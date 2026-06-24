using Winche.Database.Values;

namespace Winche.Database.Tests.Values;

public class ValueTests
{
    [Fact]
    public void TypeRank_FollowsCrossTypeTotalOrder()
    {
        // null < bool < NaN < number < timestamp < string < bytes < reference < geopoint < array < map
        short[] ranks =
        [
            (short)new NullValue().Rank,
            (short)new BooleanValue(true).Rank,
            (short)new DoubleValue(double.NaN).Rank,
            (short)new IntegerValue(1).Rank,
            (short)new TimestampValue(DateTimeOffset.UnixEpoch).Rank,
            (short)new StringValue("a").Rank,
            (short)new BytesValue([1]).Rank,
            (short)new ReferenceValue("users/u1").Rank,
            (short)new GeoPointValue(0, 0).Rank,
            (short)new ArrayValue([]).Rank,
            (short)new MapValue(new Dictionary<string, Value>()).Rank,
        ];
        Assert.Equal(ranks.OrderBy(r => r), ranks);
        Assert.Equal(ranks.Distinct().Count(), ranks.Length);
    }

    [Fact]
    public void IntegerAndDouble_ShareNumberRank()
    {
        Assert.Equal(new IntegerValue(5).Rank, new DoubleValue(5.0).Rank);
        Assert.Equal(TypeRank.Number, new DoubleValue(1.5).Rank);
    }

    [Fact]
    public void NaN_HasOwnRankBelowNumber()
    {
        Assert.Equal(TypeRank.NaN, new DoubleValue(double.NaN).Rank);
        Assert.True((short)TypeRank.NaN < (short)TypeRank.Number);
        Assert.True((short)TypeRank.NaN > (short)TypeRank.Boolean);
    }

    [Fact]
    public void ArrayValue_HasStructuralEquality()
    {
        var a = new ArrayValue([new IntegerValue(1), new StringValue("x")]);
        var b = new ArrayValue([new IntegerValue(1), new StringValue("x")]);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, new ArrayValue([new IntegerValue(2)]));
    }

    [Fact]
    public void MapValue_HasStructuralEquality_KeyOrderIndependent()
    {
        var a = new MapValue(new Dictionary<string, Value> { ["x"] = new IntegerValue(1), ["y"] = new BooleanValue(true) });
        var b = new MapValue(new Dictionary<string, Value> { ["y"] = new BooleanValue(true), ["x"] = new IntegerValue(1) });
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, new MapValue(new Dictionary<string, Value> { ["x"] = new IntegerValue(1) }));
    }

    [Fact]
    public void BytesValue_HasStructuralEquality()
    {
        Assert.Equal(new BytesValue([1, 2, 3]), new BytesValue([1, 2, 3]));
        Assert.NotEqual(new BytesValue([1, 2, 3]), new BytesValue([1, 2]));
    }

    [Fact]
    public void NestedValues_CompareStructurally()
    {
        var a = new MapValue(new Dictionary<string, Value> { ["tags"] = new ArrayValue([new StringValue("a")]) });
        var b = new MapValue(new Dictionary<string, Value> { ["tags"] = new ArrayValue([new StringValue("a")]) });
        Assert.Equal(a, b);
    }

    [Fact]
    public void TimestampValue_TruncatesToMicroseconds()
    {
        var t1 = new TimestampValue(DateTimeOffset.UnixEpoch.AddTicks(9));  // 900 ns
        var t2 = new TimestampValue(DateTimeOffset.UnixEpoch);
        Assert.Equal(t2, t1); // sub-microsecond precision is normalized away
    }
}
