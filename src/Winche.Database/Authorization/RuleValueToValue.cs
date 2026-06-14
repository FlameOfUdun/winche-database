using Winche.Database.Values;
using Winche.Rules;

namespace Winche.Database.Authorization;

/// <summary>
/// Inverse of <see cref="ValueToRuleValue"/>: maps a <see cref="RuleValue"/> back to a Winche
/// <see cref="Value"/> so comparisons run through the database's canonical <see cref="ValueComparer"/>.
/// </summary>
internal static class RuleValueToValue
{
    public static Value Convert(RuleValue value) => value.Kind switch
    {
        RuleValueKind.Null => new NullValue(),
        RuleValueKind.Bool => new BooleanValue(value.AsBool),
        RuleValueKind.Int => new IntegerValue(value.AsInt),
        RuleValueKind.Double => new DoubleValue(value.AsDouble),
        RuleValueKind.String => new StringValue(value.AsString),
        RuleValueKind.Bytes => new BytesValue(value.AsBytes),
        RuleValueKind.Timestamp => new TimestampValue(value.AsTimestamp),
        RuleValueKind.Path => new ReferenceValue(value.AsPath),
        RuleValueKind.List => new ArrayValue([.. value.AsList.Select(Convert)]),
        RuleValueKind.Map => new MapValue(value.AsMap.ToDictionary(kv => kv.Key, kv => Convert(kv.Value))),
        _ => throw new ArgumentOutOfRangeException(nameof(value), value.Kind, "Unhandled RuleValueKind."),
    };
}
