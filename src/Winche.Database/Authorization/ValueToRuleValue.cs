using Winche.Database.Values;
using Winche.Rules;

namespace Winche.Database.Authorization;

/// <summary>
/// Converts a Winche <see cref="Value"/> to the rules engine's <see cref="RuleValue"/>.
/// </summary>
internal static class ValueToRuleValue
{
    /// <summary>Converts <paramref name="value"/> to the equivalent <see cref="RuleValue"/>.</summary>
    /// <remarks>
    /// Mapping notes:
    /// <list type="bullet">
    ///   <item><see cref="NullValue"/> → <see cref="RuleValue.Null"/></item>
    ///   <item><see cref="BooleanValue"/> → <see cref="RuleValue.Bool(bool)"/></item>
    ///   <item><see cref="IntegerValue"/> → <see cref="RuleValue.Int(long)"/></item>
    ///   <item><see cref="DoubleValue"/> → <see cref="RuleValue.Double(double)"/></item>
    ///   <item><see cref="StringValue"/> → <see cref="RuleValue.String(string)"/></item>
    ///   <item><see cref="BytesValue"/> → <see cref="RuleValue.Bytes(byte[])"/></item>
    ///   <item><see cref="TimestampValue"/> → <see cref="RuleValue.Timestamp(DateTimeOffset)"/></item>
    ///   <item><see cref="ReferenceValue"/> → <see cref="RuleValue.Path(string)"/></item>
    ///   <item><see cref="GeoPointValue"/> → <see cref="RuleValue.Map"/> of {"latitude":Double,"longitude":Double}
    ///     (no exact <see cref="RuleValue"/> equivalent; encoded as a map for rule access).</item>
    ///   <item><see cref="ArrayValue"/> → <see cref="RuleValue.List(IReadOnlyList{RuleValue})"/> (recursive)</item>
    ///   <item><see cref="MapValue"/> → <see cref="RuleValue.Map(IReadOnlyDictionary{string,RuleValue})"/> (recursive)</item>
    /// </list>
    /// </remarks>
    public static RuleValue Convert(Value value) => value switch
    {
        NullValue => RuleValue.Null,
        BooleanValue bv => RuleValue.Bool(bv.Value),
        IntegerValue iv => RuleValue.Int(iv.Value),
        DoubleValue dv => RuleValue.Double(dv.Value),
        StringValue sv => RuleValue.String(sv.Value),
        BytesValue bv => RuleValue.Bytes(bv.Value),
        TimestampValue tv => RuleValue.Timestamp(tv.Value),
        ReferenceValue rv => RuleValue.Path(rv.Path),
        GeoPointValue gv => ConvertGeoPoint(gv),
        ArrayValue av => RuleValue.List(av.Values.Select(Convert).ToList()),
        MapValue mv => RuleValue.Map(mv.Fields.ToDictionary(kv => kv.Key, kv => Convert(kv.Value))),
        _ => throw new ArgumentOutOfRangeException(nameof(value), value?.GetType().Name, "Unhandled Value case."),
    };

    // GeoPoint has no direct RuleValue equivalent; encode as a two-field map so rule expressions
    // can still access latitude/longitude by name (e.g. resource.data.location.latitude).
    private static RuleValue ConvertGeoPoint(GeoPointValue gv) =>
        RuleValue.Map(new Dictionary<string, RuleValue>
        {
            ["latitude"] = RuleValue.Double(gv.Latitude),
            ["longitude"] = RuleValue.Double(gv.Longitude),
        });
}
