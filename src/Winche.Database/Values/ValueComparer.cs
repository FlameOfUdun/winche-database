namespace Winche.Database.Values;

/// <summary>
/// The C# mirror of the SQL total order (winche_* / winche_key). Rank first, then per-class:
/// numbers exactly (decimal inside ±2^63, double compare outside), strings/references by
/// Unicode CODE POINT (== UTF-8 byte order), bytes lexicographic, geopoints lat→lng,
/// arrays element-wise (prefix first), maps by sorted keys then values.
/// Compare(a,b)==0 is typed equality (int 5 == double 5.0).
/// </summary>
public sealed class ValueComparer : IComparer<Value>
{
    public static readonly ValueComparer Instance = new();
    private ValueComparer() { }

    public int Compare(Value? x, Value? y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);

        var rank = ((short)x.Rank).CompareTo((short)y.Rank);
        if (rank != 0) return rank;

        return (x, y) switch
        {
            (NullValue, NullValue) => 0,
            (BooleanValue a, BooleanValue b) => a.Value.CompareTo(b.Value),
            // both rank NaN (29) → equal; both rank Number (30) → numeric compare
            (DoubleValue a, DoubleValue b) when double.IsNaN(a.Value) && double.IsNaN(b.Value) => 0,
            (IntegerValue a, IntegerValue b) => a.Value.CompareTo(b.Value),
            (IntegerValue a, DoubleValue b) => CompareLongDouble(a.Value, b.Value),
            (DoubleValue a, IntegerValue b) => -CompareLongDouble(b.Value, a.Value),
            (DoubleValue a, DoubleValue b) => a.Value == b.Value ? 0 : a.Value.CompareTo(b.Value),
            (TimestampValue a, TimestampValue b) => a.Value.UtcTicks.CompareTo(b.Value.UtcTicks),
            (StringValue a, StringValue b) => CompareCodePoints(a.Value, b.Value),
            (BytesValue a, BytesValue b) => a.Value.AsSpan().SequenceCompareTo(b.Value),
            (ReferenceValue a, ReferenceValue b) => CompareCodePoints(a.Path, b.Path),
            (GeoPointValue a, GeoPointValue b) => a.Latitude != b.Latitude
                ? a.Latitude.CompareTo(b.Latitude)
                : a.Longitude.CompareTo(b.Longitude),
            (ArrayValue a, ArrayValue b) => CompareArrays(a.Values, b.Values),
            (MapValue a, MapValue b) => CompareMaps(a.Fields, b.Fields),
            _ => throw new NotSupportedException($"Cannot compare {x.GetType().Name} and {y.GetType().Name}"),
        };
    }

    /// <summary>Typed equality (Compare == 0).</summary>
    public bool Equals(Value x, Value y) => x.Rank == y.Rank && Compare(x, y) == 0;

    private static int CompareLongDouble(long l, double d)
    {
        // NaN never reaches here (rank 29). Outside long range the double decides.
        if (double.IsPositiveInfinity(d) || d >= 9.2233720368547758e18) return -1;
        if (double.IsNegativeInfinity(d) || d < -9.2233720368547758e18) return 1;
        // decimal flushes subnormals (|d| < ~1e-28) to 0; SQL numeric keeps their sign.
        // l==0 → 0 vs tiny: sign of d decides; l!=0: |l|>=1 >> |d|, so sign of l decides.
        var dd = (decimal)d;
        if (d != 0 && dd == 0m)
            return l == 0 ? -Math.Sign(d) : Math.Sign(l);
        return ((decimal)l).CompareTo(dd);   // both exact in decimal within this range
    }

    internal static int CompareCodePoints(string a, string b)
    {
        var ea = a.EnumerateRunes().GetEnumerator();
        var eb = b.EnumerateRunes().GetEnumerator();
        while (true)
        {
            var hasA = ea.MoveNext();
            var hasB = eb.MoveNext();
            if (!hasA || !hasB) return hasA.CompareTo(hasB);
            var c = ea.Current.Value.CompareTo(eb.Current.Value);
            if (c != 0) return c;
        }
    }

    private static int CompareArrays(IReadOnlyList<Value> a, IReadOnlyList<Value> b)
    {
        var n = Math.Min(a.Count, b.Count);
        for (var i = 0; i < n; i++)
        {
            var c = Instance.Compare(a[i], b[i]);
            if (c != 0) return c;
        }
        return a.Count.CompareTo(b.Count);
    }

    private static int CompareMaps(IReadOnlyDictionary<string, Value> a, IReadOnlyDictionary<string, Value> b)
    {
        var ka = a.Keys.OrderBy(k => k, CodePointStringComparer.Instance).ToList();
        var kb = b.Keys.OrderBy(k => k, CodePointStringComparer.Instance).ToList();
        var n = Math.Min(ka.Count, kb.Count);
        for (var i = 0; i < n; i++)
        {
            var kc = CompareCodePoints(ka[i], kb[i]);
            if (kc != 0) return kc;
            var vc = Instance.Compare(a[ka[i]], b[kb[i]]);
            if (vc != 0) return vc;
        }
        return ka.Count.CompareTo(kb.Count);
    }

    private sealed class CodePointStringComparer : IComparer<string>
    {
        public static readonly CodePointStringComparer Instance = new();
        public int Compare(string? x, string? y) => CompareCodePoints(x!, y!);
    }
}
