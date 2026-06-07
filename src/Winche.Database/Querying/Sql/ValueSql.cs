// src/Winche.Database/Querying/Sql/ValueSql.cs
using System.Globalization;
using Winche.Database.Values;

namespace Winche.Database.Querying.Sql;

/// <summary>Maps operand Values to typed SQL parameter fragments.</summary>
internal static class ValueSql
{
    /// <summary>Epoch-microseconds from a timestamp value (shared with IndexPredicateSql).</summary>
    internal static long EpochMicros(TimestampValue t) =>
        (t.Value.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10;

    /// <summary>Shortest round-trip string for a finite double (shared with IndexPredicateSql).</summary>
    internal static string DoubleRoundTrip(double d) =>
        d.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Numeric-comparable operand (matches winche_num's normalization).</summary>
    internal static string NumOperand(Value v, ParameterBag bag) => v switch
    {
        BooleanValue b => $"{bag.Add(b.Value ? 1 : 0)}::numeric",
        IntegerValue i => $"{bag.Add(i.Value)}::numeric",
        DoubleValue d => DoubleOperand(d.Value, bag),
        TimestampValue t => $"{bag.Add(EpochMicros(t))}::numeric",
        _ => throw new InvalidOperationException($"No numeric operand for {v.GetType().Name}"),
    };

    /// <summary>
    /// Doubles are bound as shortest-round-trip TEXT and cast ::numeric so Postgres parses the
    /// same 17 significant digits System.Text.Json writes into storage. Binding as a float8
    /// parameter would collapse to 15 digits (float8::numeric) and break Eq/range exactness.
    /// </summary>
    internal static string DoubleOperand(double d, ParameterBag bag) =>
        $"{bag.Add(d.ToString("R", CultureInfo.InvariantCulture))}::numeric";

    /// <summary>Operand as canonical tagged jsonb parameter (for winche_key comparisons).</summary>
    internal static string CanonicalJsonb(Value v, ParameterBag bag) =>
        bag.AddJsonb(ValueSerializer.Write(v).ToJsonString());
}
