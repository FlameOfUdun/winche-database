// tests/Winche.Database.Tests/Values/ValueComparerTests.cs
using Winche.Database.Values;

namespace Winche.Database.Tests.Values;

public class ValueComparerTests
{
    private static int C(Value a, Value b) => ValueComparer.Instance.Compare(a, b);

    [Fact]
    public void CrossType_FollowsTotalOrder()
    {
        Value[] ordered =
        [
            new NullValue(), new BooleanValue(false), new BooleanValue(true),
            new DoubleValue(double.NaN), new DoubleValue(double.NegativeInfinity),
            new IntegerValue(-1), new DoubleValue(1.5), new IntegerValue(2),
            new TimestampValue(DateTimeOffset.UnixEpoch),
            new StringValue("a"), new BytesValue([1]), new ReferenceValue("c/a"),
            new GeoPointValue(0, 0), new ArrayValue([]),
            new MapValue(new Dictionary<string, Value>()),
        ];
        for (var i = 0; i < ordered.Length - 1; i++)
            Assert.True(C(ordered[i], ordered[i + 1]) < 0, $"expected [{i}] < [{i + 1}]");
    }

    [Fact]
    public void Numbers_IntAndDoubleCompareExactly()
    {
        Assert.Equal(0, C(new IntegerValue(5), new DoubleValue(5.0)));
        Assert.True(C(new IntegerValue(5), new DoubleValue(5.5)) < 0);
        // beyond double precision: 2^53 and 2^53+1 are DIFFERENT longs
        Assert.True(C(new IntegerValue(9007199254740993), new IntegerValue(9007199254740992)) > 0);
        // long vs double straddling: 2^53+1 (long) vs 2^53 (double, exact)
        Assert.True(C(new IntegerValue(9007199254740993), new DoubleValue(9007199254740992)) > 0);
        Assert.True(C(new IntegerValue(1), new DoubleValue(double.PositiveInfinity)) < 0);
        Assert.True(C(new DoubleValue(double.NegativeInfinity), new IntegerValue(long.MinValue)) < 0);
    }

    [Fact]
    public void Strings_CodePointOrder_NotOrdinalUtf16()
    {
        Assert.True(C(new StringValue("B"), new StringValue("a")) < 0);     // byte order
        // U+FFFD (efbfbd) vs U+1F30D (f09f8c8d): code point order puts FFFD first;
        // UTF-16 ordinal would put the surrogate pair (D83C…) first — must be code points.
        Assert.True(C(new StringValue("�"), new StringValue("\U0001F30D")) < 0);
    }

    [Fact]
    public void Timestamps_CompareByInstant()
    {
        var utc = new TimestampValue(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var offset = new TimestampValue(new DateTimeOffset(2026, 1, 1, 14, 0, 0, TimeSpan.FromHours(2)));
        Assert.Equal(0, C(utc, offset));
    }

    [Fact]
    public void Arrays_ElementWise_PrefixFirst()
    {
        Assert.True(C(new ArrayValue([new IntegerValue(1)]),
                      new ArrayValue([new IntegerValue(1), new IntegerValue(0)])) < 0);
        Assert.True(C(new ArrayValue([new IntegerValue(1), new IntegerValue(9)]),
                      new ArrayValue([new IntegerValue(2)])) < 0);
    }

    [Fact]
    public void Maps_KeyThenValue()
    {
        var a1 = new MapValue(new Dictionary<string, Value> { ["a"] = new IntegerValue(1) });
        var a2 = new MapValue(new Dictionary<string, Value> { ["a"] = new IntegerValue(2) });
        var b1 = new MapValue(new Dictionary<string, Value> { ["b"] = new IntegerValue(1) });
        Assert.True(C(a1, a2) < 0);
        Assert.True(C(a2, b1) < 0);
    }

    [Fact]
    public void GeoPoints_LatThenLng()
    {
        Assert.True(C(new GeoPointValue(1, 9), new GeoPointValue(2, 0)) < 0);
        Assert.True(C(new GeoPointValue(1, 1), new GeoPointValue(1, 2)) < 0);
    }

    [Fact]
    public void Bytes_Lexicographic()
    {
        Assert.True(C(new BytesValue([1]), new BytesValue([1, 0])) < 0);
        Assert.True(C(new BytesValue([1, 255]), new BytesValue([2])) < 0);
    }

    [Fact]
    public void NegativeZero_EqualsZero_AcrossTypes()
    {
        Assert.Equal(0, C(new DoubleValue(-0.0), new DoubleValue(0.0)));
        Assert.Equal(0, C(new DoubleValue(-0.0), new IntegerValue(0)));
        Assert.Equal(0, C(new IntegerValue(0), new DoubleValue(-0.0)));
    }

    [Fact]
    public void TinyDouble_FlushesToZero_StillOrdered()
    {
        // 1e-300 is a subnormal-ish tiny positive that decimal casts to 0m
        // 0 (int) < 1e-300 (double) in SQL numeric; our comparer must agree
        Assert.True(C(new IntegerValue(0), new DoubleValue(1e-300)) < 0);
        Assert.True(C(new IntegerValue(0), new DoubleValue(-1e-300)) > 0);
    }
}
