using System.Globalization;
using System.Text.Json.Nodes;

namespace Winche.Database.Values;

/// <summary>
/// Value ↔ Firestore-style wire JSON ({"integerValue":"42"}, {"mapValue":{"fields":{…}}}, …).
/// The same encoding is used for storage (see Documents/StorageCodec).
/// </summary>
public static class ValueSerializer
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'";

    public static JsonObject Write(Value value) => value switch
    {
        NullValue => new JsonObject { ["nullValue"] = null },
        BooleanValue b => new JsonObject { ["booleanValue"] = b.Value },
        IntegerValue i => new JsonObject { ["integerValue"] = i.Value.ToString(CultureInfo.InvariantCulture) },
        DoubleValue d => new JsonObject { ["doubleValue"] = WriteDouble(d.Value) },
        TimestampValue t => new JsonObject { ["timestampValue"] = t.Value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture) },
        StringValue s => new JsonObject { ["stringValue"] = s.Value },
        BytesValue by => new JsonObject { ["bytesValue"] = Convert.ToBase64String(by.Value) },
        ReferenceValue r => new JsonObject { ["referenceValue"] = r.Path },
        GeoPointValue g => new JsonObject { ["geoPointValue"] = new JsonObject { ["latitude"] = g.Latitude, ["longitude"] = g.Longitude } },
        ArrayValue a => new JsonObject { ["arrayValue"] = new JsonObject { ["values"] = new JsonArray([.. a.Values.Select(v => (JsonNode)Write(v))]) } },
        MapValue m => new JsonObject { ["mapValue"] = new JsonObject { ["fields"] = WriteFields(m.Fields) } },
        _ => throw new NotSupportedException($"Unknown Value type: {value.GetType().Name}")
    };

    public static JsonObject WriteFields(IReadOnlyDictionary<string, Value> fields)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in fields.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            obj[key] = Write(value);
        return obj;
    }

    private static JsonNode WriteDouble(double d)
    {
        if (double.IsNaN(d)) return "NaN";
        if (double.IsPositiveInfinity(d)) return "Infinity";
        if (double.IsNegativeInfinity(d)) return "-Infinity";
        return d;
    }

    public static Value Read(JsonNode node)
    {
        if (node is not JsonObject obj)
            throw new WireFormatException($"Expected a tagged value object, got: {node.GetValueKind()}");
        if (obj.Count != 1)
            throw new WireFormatException(
                $"Expected exactly one type tag, got {obj.Count}: {string.Join(", ", obj.Select(p => p.Key))}");

        var (tag, payload) = obj.First();
        return tag switch
        {
            "nullValue" => new NullValue(),
            "booleanValue" => new BooleanValue(GetBool(payload)),
            "integerValue" => new IntegerValue(GetInt64(payload)),
            "doubleValue" => new DoubleValue(GetDouble(payload)),
            "timestampValue" => new TimestampValue(GetTimestamp(payload)),
            "stringValue" => new StringValue(GetString(payload, tag)),
            "bytesValue" => new BytesValue(GetBytes(payload)),
            "referenceValue" => new ReferenceValue(GetString(payload, tag)),
            "geoPointValue" => ReadGeoPoint(payload),
            "arrayValue" => ReadArray(payload),
            "mapValue" => ReadMap(payload),
            _ => throw new WireFormatException($"Unknown value tag: '{tag}'")
        };
    }

    public static IReadOnlyDictionary<string, Value> ReadFields(JsonObject fields)
    {
        var result = new Dictionary<string, Value>(fields.Count);
        foreach (var (key, value) in fields)
            result[key] = Read(value ?? throw new WireFormatException($"Field '{key}' has no value"));
        return result;
    }

    private static bool GetBool(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<bool>(out var b)
            ? b : throw new WireFormatException("booleanValue must be a JSON boolean");

    private static long GetInt64(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<string>(out var s) && long.TryParse(s, CultureInfo.InvariantCulture, out var p)) return p;
        }
        throw new WireFormatException("integerValue must be an int64 (string or number)");
    }

    private static double GetDouble(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<string>(out var s))
                switch (s)
                {
                    case "NaN": return double.NaN;
                    case "Infinity": return double.PositiveInfinity;
                    case "-Infinity": return double.NegativeInfinity;
                }
        }
        throw new WireFormatException("doubleValue must be a JSON number or 'NaN'/'Infinity'/'-Infinity'");
    }

    private static DateTimeOffset GetTimestamp(JsonNode? n)
    {
        var s = GetString(n, "timestampValue");
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto)
            ? dto : throw new WireFormatException($"Invalid timestampValue: '{s}'");
    }

    private static string GetString(JsonNode? n, string tag) =>
        n is JsonValue v && v.TryGetValue<string>(out var s)
            ? s : throw new WireFormatException($"{tag} must be a JSON string");

    private static byte[] GetBytes(JsonNode? n)
    {
        var s = GetString(n, "bytesValue");
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { throw new WireFormatException("bytesValue must be valid base64"); }
    }

    private static GeoPointValue ReadGeoPoint(JsonNode? n)
    {
        if (n is not JsonObject o
            || o["latitude"] is not JsonValue lat || !lat.TryGetValue<double>(out var la)
            || o["longitude"] is not JsonValue lng || !lng.TryGetValue<double>(out var lo))
            throw new WireFormatException("geoPointValue must be {latitude, longitude}");
        return new GeoPointValue(la, lo);
    }

    private static ArrayValue ReadArray(JsonNode? n)
    {
        if (n is not JsonObject o) throw new WireFormatException("arrayValue must be an object");
        if (o["values"] is null) return new ArrayValue([]);          // Firestore omits empty values
        if (o["values"] is not JsonArray arr) throw new WireFormatException("arrayValue.values must be an array");
        return new ArrayValue([.. arr.Select(e => Read(e ?? throw new WireFormatException("array element is null")))]);
    }

    private static MapValue ReadMap(JsonNode? n)
    {
        if (n is not JsonObject o) throw new WireFormatException("mapValue must be an object");
        if (o["fields"] is null) return new MapValue(new Dictionary<string, Value>()); // Firestore omits empty fields
        if (o["fields"] is not JsonObject fields) throw new WireFormatException("mapValue.fields must be an object");
        return new MapValue(ReadFields(fields));
    }
}
