using System.Text.Json;
using System.Text.Json.Nodes;

namespace WincheDb.JsonSerialization.Parsers;

internal static class CursorValueParser
{
    public static List<object?> Parse(JsonArray? arr)
    {
        if (arr is null) 
            return [];

        return [.. arr.Select(ToClr)];
    }

    private static object? ToClr(JsonNode? node) => node switch
    {
        null => null,
        JsonArray ja => Parse(ja),       // recursive nested arrays
        JsonObject => null,             // objects invalid as cursor values
        JsonValue jv => ValueToClr(jv),
        _ => null
    };

    private static object? NodeToClr(JsonNode? node) => node switch
    {
        null => null,
        JsonArray ja => ja.Select(NodeToClr).ToList(),
        JsonObject jo => jo.ToDictionary(kv => kv.Key, kv => NodeToClr(kv.Value)),
        JsonValue jv => ValueToClr(jv),
        _ => null
    };

    private static object? ValueToClr(JsonValue jv) => jv.GetValueKind() switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => jv.GetValue<string>(),
        JsonValueKind.Number => ParseNumberElement(jv.GetValue<JsonElement>()),
        _ => null
    };

    private static object ParseNumberElement(JsonElement el)
    {
        if (el.TryGetInt64(out var l)) return l;
        if (el.TryGetDecimal(out var d)) return d;
        return el.GetDouble();
    }
}