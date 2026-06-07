// tests/Winche.Database.Tests/Runtime/TransformApplierTests.cs
using Winche.Database.Documents;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.Tests.Runtime;

public class TransformApplierTests
{
    private static readonly DateTimeOffset Commit = new(2026, 6, 7, 12, 0, 0, TimeSpan.Zero);
    private static FieldPath F(string p) => FieldPath.Parse(p);

    private static Value Apply(TransformKind kind, Value? existing, Value? operand) =>
        TransformApplier.Apply(existing, new FieldTransform(F("f"), kind, operand), Commit);

    // ── ServerTimestamp ───────────────────────────────────────────────────────

    [Fact]
    public void ServerTimestamp_IsCommitTime() =>
        Assert.Equal(new TimestampValue(Commit), Apply(TransformKind.ServerTimestamp, new IntegerValue(1), null));

    // ── Increment ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(5L, 3L, 8L)]
    [InlineData(-5L, 3L, -2L)]
    public void Increment_IntInt_StaysInteger(long existing, long op, long expected) =>
        Assert.Equal(new IntegerValue(expected), Apply(TransformKind.Increment, new IntegerValue(existing), new IntegerValue(op)));

    [Fact]
    public void Increment_Saturates_AtLongBounds()
    {
        Assert.Equal(new IntegerValue(long.MaxValue),
            Apply(TransformKind.Increment, new IntegerValue(long.MaxValue), new IntegerValue(1)));
        Assert.Equal(new IntegerValue(long.MinValue),
            Apply(TransformKind.Increment, new IntegerValue(long.MinValue), new IntegerValue(-1)));
    }

    [Fact]
    public void Increment_AnyDouble_BecomesDouble()
    {
        Assert.Equal(new DoubleValue(8.5), Apply(TransformKind.Increment, new IntegerValue(5), new DoubleValue(3.5)));
        Assert.Equal(new DoubleValue(8.5), Apply(TransformKind.Increment, new DoubleValue(5.5), new IntegerValue(3)));
    }

    [Fact]
    public void Increment_MissingOrNonNumber_BecomesOperand()
    {
        Assert.Equal(new IntegerValue(3), Apply(TransformKind.Increment, null, new IntegerValue(3)));
        Assert.Equal(new IntegerValue(3), Apply(TransformKind.Increment, new StringValue("x"), new IntegerValue(3)));
        Assert.Equal(new IntegerValue(3), Apply(TransformKind.Increment, new NullValue(), new IntegerValue(3)));
    }

    // ── Maximum / Minimum ─────────────────────────────────────────────────────

    [Fact]
    public void MaxMin_NumericComparison_KeepsWinner()
    {
        Assert.Equal(new IntegerValue(7), Apply(TransformKind.Maximum, new IntegerValue(7), new IntegerValue(3)));
        Assert.Equal(new DoubleValue(7.5), Apply(TransformKind.Maximum, new IntegerValue(7), new DoubleValue(7.5)));
        Assert.Equal(new IntegerValue(3), Apply(TransformKind.Minimum, new IntegerValue(7), new IntegerValue(3)));
    }

    [Fact]
    public void MaxMin_MissingOrNonNumber_BecomesOperand()
    {
        Assert.Equal(new IntegerValue(3), Apply(TransformKind.Maximum, null, new IntegerValue(3)));
        Assert.Equal(new IntegerValue(3), Apply(TransformKind.Minimum, new StringValue("z"), new IntegerValue(3)));
    }

    [Fact]
    public void MaxMin_NaN_FollowsNumberOrdering()
    {
        // NaN ranks below all numbers: max(NaN, 1) = 1, min(NaN, 1) = NaN
        Assert.Equal(new IntegerValue(1), Apply(TransformKind.Maximum, new DoubleValue(double.NaN), new IntegerValue(1)));
        Assert.Equal(new DoubleValue(double.NaN), Apply(TransformKind.Minimum, new DoubleValue(double.NaN), new IntegerValue(1)));
    }

    // ── ArrayUnion / ArrayRemove ──────────────────────────────────────────────

    [Fact]
    public void ArrayUnion_AppendsMissing_TypedEquality_DedupesOperand()
    {
        var existing = new ArrayValue([new IntegerValue(1), new StringValue("a")]);
        var operand = new ArrayValue([new DoubleValue(1.0), new StringValue("b"), new StringValue("b")]);
        var result = Assert.IsType<ArrayValue>(Apply(TransformKind.ArrayUnion, existing, operand));
        // 1.0 == 1 (typed equality) → not appended; "b" appended once
        Assert.Equal(3, result.Values.Count);
        Assert.Equal(new StringValue("b"), result.Values[2]);
    }

    [Fact]
    public void ArrayUnion_NonArrayExisting_BecomesOperandArray()
    {
        var operand = new ArrayValue([new IntegerValue(1), new IntegerValue(1)]);
        var result = Assert.IsType<ArrayValue>(Apply(TransformKind.ArrayUnion, new StringValue("x"), operand));
        Assert.Single(result.Values);                            // operand deduped
    }

    [Fact]
    public void ArrayRemove_RemovesAllTypedEqual()
    {
        var existing = new ArrayValue([new IntegerValue(1), new StringValue("a"), new DoubleValue(1.0)]);
        var result = Assert.IsType<ArrayValue>(Apply(TransformKind.ArrayRemove, existing,
            new ArrayValue([new IntegerValue(1)])));
        Assert.Equal([new StringValue("a")], result.Values);
    }

    [Fact]
    public void ArrayRemove_NonArrayOrMissing_BecomesEmptyArray()
    {
        Assert.Empty(Assert.IsType<ArrayValue>(
            Apply(TransformKind.ArrayRemove, new StringValue("x"), new ArrayValue([new IntegerValue(1)]))).Values);
        Assert.Empty(Assert.IsType<ArrayValue>(
            Apply(TransformKind.ArrayRemove, null, new ArrayValue([new IntegerValue(1)]))).Values);
    }
}
