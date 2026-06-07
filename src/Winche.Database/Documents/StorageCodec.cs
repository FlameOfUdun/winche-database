using System.Text.Json.Nodes;
using Winche.Database.Values;

namespace Winche.Database.Documents;

/// <summary>
/// Encodes/decodes a typed field map to/from the `data` jsonb column.
/// Storage form is the tagged field map directly (no "fields" wrapper):
/// {"age":{"integerValue":"30"}, ...}
/// </summary>
public static class StorageCodec
{
    public static string Encode(IReadOnlyDictionary<string, Value> fields) =>
        ValueSerializer.WriteFields(fields).ToJsonString();

    public static IReadOnlyDictionary<string, Value> Decode(string json)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException ex)
        {
            throw new WireFormatException($"Stored document data is not valid JSON: {ex.Message}");
        }

        if (node is not JsonObject obj)
            throw new WireFormatException("Stored document data must be a JSON object.");
        return ValueSerializer.ReadFields(obj);
    }
}
