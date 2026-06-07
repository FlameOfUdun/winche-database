using System.Text.Json.Serialization;

namespace Winche.Database.Values;

[JsonConverter(typeof(Winche.Database.Querying.Ast.Serialization.ValueJsonConverter))]
public abstract record Value
{
    public abstract TypeRank Rank { get; }
}

public sealed record NullValue : Value
{
    public override TypeRank Rank => TypeRank.Null;
}

public sealed record BooleanValue(bool Value) : Value
{
    public override TypeRank Rank => TypeRank.Boolean;
}

public sealed record IntegerValue(long Value) : Value
{
    public override TypeRank Rank => TypeRank.Number;
}

public sealed record DoubleValue(double Value) : Value
{
    public override TypeRank Rank => double.IsNaN(Value) ? TypeRank.NaN : TypeRank.Number;
}

public sealed record TimestampValue : Value
{
    public DateTimeOffset Value { get; }

    public TimestampValue(DateTimeOffset value) =>
        Value = new DateTimeOffset(value.Ticks - value.Ticks % 10, value.Offset); // truncate to µs (1 tick = 100ns)

    public override TypeRank Rank => TypeRank.Timestamp;
}

public sealed record StringValue(string Value) : Value
{
    public override TypeRank Rank => TypeRank.String;
}

public sealed record BytesValue(byte[] Value) : Value
{
    public override TypeRank Rank => TypeRank.Bytes;

    public bool Equals(BytesValue? other) =>
        other is not null && Value.AsSpan().SequenceEqual(other.Value);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(Value);
        return hash.ToHashCode();
    }
}

public sealed record ReferenceValue(string Path) : Value
{
    public override TypeRank Rank => TypeRank.Reference;
}

public sealed record GeoPointValue(double Latitude, double Longitude) : Value
{
    public override TypeRank Rank => TypeRank.GeoPoint;
}

public sealed record ArrayValue(IReadOnlyList<Value> Values) : Value
{
    public override TypeRank Rank => TypeRank.Array;

    public bool Equals(ArrayValue? other) =>
        other is not null && Values.SequenceEqual(other.Values);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var v in Values) hash.Add(v);
        return hash.ToHashCode();
    }
}

public sealed record MapValue(IReadOnlyDictionary<string, Value> Fields) : Value
{
    public override TypeRank Rank => TypeRank.Map;

    public bool Equals(MapValue? other) =>
        other is not null
        && Fields.Count == other.Fields.Count
        && Fields.All(kv => other.Fields.TryGetValue(kv.Key, out var v) && kv.Value.Equals(v));

    public override int GetHashCode()
    {
        // order-independent: XOR of per-entry hashes
        var hash = 0;
        foreach (var (k, v) in Fields) hash ^= HashCode.Combine(k, v);
        return hash;
    }
}
