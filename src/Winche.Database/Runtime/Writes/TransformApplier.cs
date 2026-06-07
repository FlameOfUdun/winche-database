using Winche.Database.Values;

namespace Winche.Database.Runtime.Writes;

/// <summary>
/// Pure transform application (spec §2). Operand shapes were validated by WriteValidator.
/// "Number" = Rank ∈ {NaN, Number}; typed equality / ordering via ValueComparer.
/// </summary>
public static class TransformApplier
{
    public static Value Apply(Value? existing, FieldTransform transform, DateTimeOffset commitTime) =>
        transform.Kind switch
        {
            TransformKind.ServerTimestamp => new TimestampValue(commitTime),
            TransformKind.Increment => Increment(existing, transform.Operand!),
            TransformKind.Maximum => MaxMin(existing, transform.Operand!, max: true),
            TransformKind.Minimum => MaxMin(existing, transform.Operand!, max: false),
            TransformKind.ArrayUnion => ArrayUnion(existing, (ArrayValue)transform.Operand!),
            TransformKind.ArrayRemove => ArrayRemove(existing, (ArrayValue)transform.Operand!),
            _ => throw new NotSupportedException($"Unknown transform: {transform.Kind}"),
        };

    private static bool IsNumber(Value? v) => v is IntegerValue or DoubleValue;

    private static Value Increment(Value? existing, Value operand)
    {
        if (!IsNumber(existing)) return operand;

        if (existing is IntegerValue ei && operand is IntegerValue oi)
        {
            // saturating long addition
            var sum = unchecked(ei.Value + oi.Value);
            var overflowed = ((ei.Value ^ sum) & (oi.Value ^ sum)) < 0;
            return new IntegerValue(overflowed ? (ei.Value > 0 ? long.MaxValue : long.MinValue) : sum);
        }

        var a = existing switch { IntegerValue i => (double)i.Value, DoubleValue d => d.Value, _ => 0 };
        var b = operand switch { IntegerValue i => (double)i.Value, DoubleValue d => d.Value, _ => 0 };
        return new DoubleValue(a + b);
    }

    private static Value MaxMin(Value? existing, Value operand, bool max)
    {
        if (!IsNumber(existing)) return operand;
        var cmp = ValueComparer.Instance.Compare(existing!, operand);
        return max ? (cmp >= 0 ? existing! : operand) : (cmp <= 0 ? existing! : operand);
    }

    private static Value ArrayUnion(Value? existing, ArrayValue operand)
    {
        var result = existing is ArrayValue arr ? new List<Value>(arr.Values) : [];
        foreach (var candidate in operand.Values)
            if (!result.Any(e => TypedEquals(e, candidate)))
                result.Add(candidate);
        return new ArrayValue(result);
    }

    private static Value ArrayRemove(Value? existing, ArrayValue operand)
    {
        if (existing is not ArrayValue arr) return new ArrayValue([]);
        return new ArrayValue([.. arr.Values.Where(e => !operand.Values.Any(o => TypedEquals(e, o)))]);
    }

    private static bool TypedEquals(Value a, Value b) =>
        a.Rank == b.Rank && ValueComparer.Instance.Compare(a, b) == 0;
}
