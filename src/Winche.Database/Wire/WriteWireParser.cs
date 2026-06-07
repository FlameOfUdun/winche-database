using System.Text.Json.Nodes;
using Winche.Database.Documents;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.Wire;

/// <summary>writes[] wire → Write[]. Shape errors are INVALID_ARGUMENT; semantic rules stay in WriteValidator.</summary>
public static class WriteWireParser
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

    public static IReadOnlyList<Write> Parse(JsonArray writes)
    {
        var result = new List<Write>(writes.Count);
        for (var i = 0; i < writes.Count; i++)
        {
            if (writes[i] is not JsonObject obj)
                throw Invalid($"writes[{i}] must be an object");

            var shapes = new[] { "set", "update", "delete" }.Where(obj.ContainsKey).ToList();
            if (shapes.Count != 1)
                throw Invalid($"writes[{i}] must be exactly one of set|update|delete");
            if (obj[shapes[0]] is not JsonObject body)
                throw Invalid($"writes[{i}].{shapes[0]} must be an object");

            result.Add(shapes[0] switch
            {
                "set" => ParseSet(body, i),
                "update" => ParseUpdate(body, i),
                _ => ParseDelete(body, i),
            });
        }
        return result;
    }

    private static SetWrite ParseSet(JsonObject body, int i) => new()
    {
        Path = GetString(body, "path", i),
        Fields = ParseFields(body["fields"], i),
        Merge = body["merge"]?.GetValue<bool>() ?? false,
        Transforms = ParseTransforms(body["transforms"], i),
        Precondition = ParsePrecondition(body["precondition"], i),
    };

    private static UpdateWrite ParseUpdate(JsonObject body, int i)
    {
        if (body["fields"] is not JsonObject fieldsObj)
            throw Invalid($"writes[{i}].update.fields must be an object");

        var fields = new Dictionary<FieldPath, Value>();
        foreach (var (key, valueNode) in fieldsObj)
        {
            FieldPath path;
            try { path = FieldPath.Parse(key); }
            catch (ArgumentException ex) { throw Invalid($"writes[{i}].update.fields: {ex.Message}"); }
            fields[path] = ParseValueOrSentinel(valueNode, i);
        }

        return new UpdateWrite
        {
            Path = GetString(body, "path", i),
            Fields = fields,
            Transforms = ParseTransforms(body["transforms"], i),
            Precondition = ParsePrecondition(body["precondition"], i),
        };
    }

    private static DeleteWrite ParseDelete(JsonObject body, int i) => new()
    {
        Path = GetString(body, "path", i),
        Cascade = body["cascade"]?.GetValue<bool>() ?? false,
        Precondition = ParsePrecondition(body["precondition"], i),
    };

    private static IReadOnlyDictionary<string, Value> ParseFields(JsonNode? node, int i)
    {
        if (node is not JsonObject obj)
            throw Invalid($"writes[{i}].fields must be an object");
        var fields = new Dictionary<string, Value>(obj.Count);
        foreach (var (key, valueNode) in obj)
            fields[key] = ParseValueOrSentinel(valueNode, i);
        return fields;
    }

    private static Value ParseValueOrSentinel(JsonNode? node, int i)
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
                    fields[key] = ParseValueOrSentinel(valueNode, i);
                return new MapValue(fields);
            }
        }

        try
        {
            return ValueSerializer.Read(node ?? throw Invalid($"writes[{i}]: field value is null"));
        }
        catch (WireFormatException ex) { throw Invalid($"writes[{i}]: {ex.Message}"); }
    }

    private static IReadOnlyList<FieldTransform>? ParseTransforms(JsonNode? node, int i)
    {
        if (node is null) return null;
        if (node is not JsonArray arr)
            throw Invalid($"writes[{i}].transforms must be an array");

        var transforms = new List<FieldTransform>(arr.Count);
        foreach (var t in arr)
        {
            if (t is not JsonObject obj)
                throw Invalid($"writes[{i}].transforms: entry must be an object");
            var kindStr = GetString(obj, "kind", i);
            if (!Kinds.TryGetValue(kindStr, out var kind))
                throw Invalid($"writes[{i}].transforms: unknown kind '{kindStr}'");

            Value? operand = null;
            if (obj["operand"] is { } op)
            {
                try { operand = ValueSerializer.Read(op); }
                catch (WireFormatException ex) { throw Invalid($"writes[{i}].transforms: {ex.Message}"); }
            }

            FieldPath field;
            try { field = FieldPath.Parse(GetString(obj, "field", i)); }
            catch (ArgumentException ex) { throw Invalid($"writes[{i}].transforms: {ex.Message}"); }

            transforms.Add(new FieldTransform(field, kind, operand));
        }
        return transforms;
    }

    private static Precondition? ParsePrecondition(JsonNode? node, int i)
    {
        if (node is null) return null;
        if (node is not JsonObject obj)
            throw Invalid($"writes[{i}].precondition must be an object");

        bool? exists = null;
        if (obj["exists"] is { } existsNode)
        {
            if (existsNode is JsonValue ev && ev.TryGetValue<bool>(out var e))
                exists = e;
            else
                throw Invalid($"writes[{i}].precondition.exists must be a boolean");
        }

        DateTimeOffset? updateTime = null;
        if (obj["updateTime"] is { } updateTimeNode)
        {
            if (updateTimeNode is JsonValue uv && uv.TryGetValue<string>(out var s))
            {
                if (!DateTimeOffset.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t))
                    throw Invalid($"writes[{i}].precondition.updateTime is not a valid timestamp");
                updateTime = t;
            }
            else
                throw Invalid($"writes[{i}].precondition.updateTime must be a string");
        }

        return new Precondition(exists, updateTime);
    }

    private static string GetString(JsonObject obj, string key, int i) =>
        obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0
            ? s : throw Invalid($"writes[{i}]: '{key}' is required");

    private static RuntimeException Invalid(string message) =>
        new(RuntimeStatus.InvalidArgument, message);
}
