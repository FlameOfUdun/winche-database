using Winche.Database.Authorization;
using Winche.Rules;
using Winche.Values;
using Xunit;

namespace Winche.Database.IntegrationTests;

public class RuleComparerConformanceTests
{
    // Representative values spanning ranks; the string pair below distinguishes code-UNIT order
    // (old Value.CompareOrdinal) from code-POINT order (the engine's ValueComparer).
    private static readonly Value[] Samples =
    [
        new IntegerValue(5), new DoubleValue(5.0), new DoubleValue(4.5),
        new StringValue("Z"),
        new StringValue("Ａ"),        // U+FF21: code unit 0xFF21 > high-surrogate 0xD800
        new StringValue("\U00010000"),    // astral U+10000: UTF-16 = 0xD800 0xDC00
        new BooleanValue(true),
        new TimestampValue(DateTimeOffset.UnixEpoch),
    ];

    [Fact]
    public void Comparer_OrderingMatches_ValueComparer()
    {
        foreach (var a in Samples)
        foreach (var b in Samples)
        {
            var engineEqual = a.Rank == b.Rank && ValueComparer.Instance.Compare(a, b) == 0;
            Assert.Equal(engineEqual, RuleValues.AreEqual(a, b));

            var sameRank = a.Rank == b.Rank;
            var orderable = RuleValues.TryCompare(a, b, out var got);
            Assert.Equal(sameRank, orderable);
            if (orderable)
                Assert.Equal(Math.Sign(ValueComparer.Instance.Compare(a, b)), Math.Sign(got));
        }
    }

    [Fact]
    public void Comparer_AstralString_OrdersByCodePoint_NotCodeUnit()
    {
        var bmp = new StringValue("Ａ");     // U+FF21
        var astral = new StringValue("\U00010000"); // U+10000

        Assert.True(RuleValues.TryCompare(bmp, astral, out var c));
        Assert.True(c < 0);                                           // code-point: U+FF21 < U+10000 (matches engine)
        Assert.True(string.CompareOrdinal("Ａ", "\U00010000") > 0); // code-unit order is the OPPOSITE (old struct bug)
    }
}
