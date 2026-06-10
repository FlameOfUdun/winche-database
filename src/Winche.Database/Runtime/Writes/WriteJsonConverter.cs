using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Winche.Database.Documents;
using Winche.Database.Values;

namespace Winche.Database.Runtime.Writes;

/// <summary>
/// STJ converter for the <see cref="Write"/> oneof wire format:
///   { "set": {…} } | { "update": {…} } | { "delete": {…} }
/// Shape errors throw <see cref="JsonException"/> so both transports (WS + REST) surface
/// them as INVALID_ARGUMENT via <see cref="Winche.Database.Wire.ErrorMapper"/>.
///
/// Serialization is not supported: writes are inbound-only on the wire.
/// </summary>
public sealed class WriteJsonConverter : JsonConverter<Write>
{
    private static readonly Dictionary<string, TransformKind> Kinds = new(StringComparer.Ordinal)
    {
        ["serverTimestamp"] = TransformKind.ServerTimestamp,
        ["increment"] = TransformKind.Increment,
        ["maximum"] = TransformKind.Maximum,
        ["minimum"] = TransformKind.Minimum,
        ["arrayUnion"] = TransformKind.ArrayUnion,
        ["arrayRemove"] = TransformKind.ArrayRemove,
    };

    // Context index is unknown at converter level; use a sentinel to keep error messages consistent.
    // When called via IReadOnlyList<Write> deserialization the individual element has no index visible
    // here, so messages say "write" rather than "writes[i]". That is acceptable: the messages are
    // identical in shape to what WriteWireParser produced.
    private const string Ctx = "write";

    public override Write Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (JsonNode.Parse(ref reader) is not JsonObject obj)
            throw new JsonException($"{Ctx} must be an object");

        var shapes = new[] { "set", "update", "delete" }.Where(obj.ContainsKey).ToList();
        if (shapes.Count != 1)
            throw new JsonException($"{Ctx} must be exactly one of set|update|delete");
        if (obj[shapes[0]] is not JsonObject body)
            throw new JsonException($"{Ctx}.{shapes[0]} must be an object");

        return shapes[0] switch
        {
            "set" => ParseSet(body),
            "update" => ParseUpdate(body),
            _ => ParseDelete(body),
        };
    }

    /// <summary>
    /// Writes are inbound-only: the wire never serializes a Write back to the client.
    /// If this is ever called it is a programming error in the server.
    /// </summary>
    public override void Write(Utf8JsonWriter writer, Write value, JsonSerializerOptions options) =>
        throw new NotSupportedException(
            "Write objects are inbound-only and cannot be serialized to the wire.");

    // ── per-shape parsers ────────────────────────────────────────────────────

    private static SetWrite ParseSet(JsonObject body) => new()
    {
        Path = GetString(body, "path"),
        Fields = ParseFields(body["fields"]),
        Merge = body["merge"]?.GetValue<bool>() ?? false,
        Transforms = ParseTransforms(body["transforms"]),
        Precondition = ParsePrecondition(body["precondition"]),
    };

    private static UpdateWrite ParseUpdate(JsonObject body)
    {
        if (body["fields"] is not JsonObject fieldsObj)
            throw new JsonException($"{Ctx}.update.fields must be an object");

        var fields = new Dictionary<FieldPath, Value>();
        foreach (var (key, valueNode) in fieldsObj)
        {
            FieldPath path;
            try { path = FieldPath.Parse(key); }
            catch (ArgumentException ex) { throw new JsonException($"{Ctx}.update.fields: {ex.Message}", ex); }
            fields[path] = ParseValueOrSentinel(valueNode);
        }

        return new UpdateWrite
        {
            Path = GetString(body, "path"),
            Fields = fields,
            Transforms = ParseTransforms(body["transforms"]),
            Precondition = ParsePrecondition(body["precondition"]),
        };
    }

    private static DeleteWrite ParseDelete(JsonObject body) => new()
    {
        Path = GetString(body, "path"),
        Cascade = body["cascade"]?.GetValue<bool>() ?? false,
        Precondition = ParsePrecondition(body["precondition"]),
    };

    // ── field parsing ────────────────────────────────────────────────────────

    private static IReadOnlyDictionary<string, Value> ParseFields(JsonNode? node)
    {
        if (node is not JsonObject obj)
            throw new JsonException($"{Ctx}.fields must be an object");
        var fields = new Dictionary<string, Value>(obj.Count);
        foreach (var (key, valueNode) in obj)
            fields[key] = ParseValueOrSentinel(valueNode);
        return fields;
    }

    private static Value ParseValueOrSentinel(JsonNode? node)
    {
        if (node is JsonObject { Count: 1 } o)
        {
            // deleteField sentinel
            if (o["deleteField"] is JsonValue dv && dv.TryGetValue<bool>(out var del) && del)
                return DeleteFieldValue.Instance;

            // Recurse into mapValue so sentinels inside nested maps are recognized
            if (o["mapValue"] is JsonObject mv && mv["fields"] is JsonObject mapFields)
            {
                var fields = new Dictionary<string, Value>(mapFields.Count);
                foreach (var (key, valueNode) in mapFields)
                    fields[key] = ParseValueOrSentinel(valueNode);
                return new MapValue(fields);
            }
        }

        try
        {
            return ValueSerializer.Read(node ?? throw new JsonException($"{Ctx}: field value is null"));
        }
        catch (WireFormatException ex) { throw new JsonException($"{Ctx}: {ex.Message}", ex); }
    }

    // ── transforms ──────────────────────────────────────────────────────────

    private static IReadOnlyList<FieldTransform>? ParseTransforms(JsonNode? node)
    {
        if (node is null) return null;
        if (node is not JsonArray arr)
            throw new JsonException($"{Ctx}.transforms must be an array");

        var transforms = new List<FieldTransform>(arr.Count);
        foreach (var t in arr)
        {
            if (t is not JsonObject obj)
                throw new JsonException($"{Ctx}.transforms: entry must be an object");
            var kindStr = GetString(obj, "kind");
            if (!Kinds.TryGetValue(kindStr, out var kind))
                throw new JsonException($"{Ctx}.transforms: unknown kind '{kindStr}'");

            Value? operand = null;
            if (obj["operand"] is { } op)
            {
                try { operand = ValueSerializer.Read(op); }
                catch (WireFormatException ex) { throw new JsonException($"{Ctx}.transforms: {ex.Message}", ex); }
            }

            FieldPath field;
            try { field = FieldPath.Parse(GetString(obj, "field")); }
            catch (ArgumentException ex) { throw new JsonException($"{Ctx}.transforms: {ex.Message}", ex); }

            transforms.Add(new FieldTransform(field, kind, operand));
        }
        return transforms;
    }

    // ── precondition ────────────────────────────────────────────────────────

    private static Precondition? ParsePrecondition(JsonNode? node)
    {
        if (node is null) return null;
        if (node is not JsonObject obj)
            throw new JsonException($"{Ctx}.precondition must be an object");

        bool? exists = null;
        if (obj["exists"] is { } existsNode)
        {
            if (existsNode is JsonValue ev && ev.TryGetValue<bool>(out var e))
                exists = e;
            else
                throw new JsonException($"{Ctx}.precondition.exists must be a boolean");
        }

        DateTimeOffset? updateTime = null;
        if (obj["updateTime"] is { } updateTimeNode)
        {
            if (updateTimeNode is JsonValue uv && uv.TryGetValue<string>(out var s))
            {
                if (!DateTimeOffset.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t))
                    throw new JsonException($"{Ctx}.precondition.updateTime is not a valid timestamp");
                updateTime = t;
            }
            else
                throw new JsonException($"{Ctx}.precondition.updateTime must be a string");
        }

        return new Precondition(exists, updateTime);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string GetString(JsonObject obj, string key) =>
        obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0
            ? s : throw new JsonException($"{Ctx}: '{key}' is required");
}
