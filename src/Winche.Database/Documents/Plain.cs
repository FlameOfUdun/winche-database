using System.Text.Json.Nodes;
using Winche.Database.Values;

namespace Winche.Database.Documents;

/// <summary>
/// Documents as plain JSON, for webhook receivers and functions: numbers, strings, timestamps as ISO text, references
/// as paths, bytes as base64.
/// </summary>
public static class Plain
{
    public static JsonObject? Of(Document? d) => d is null ? null : new JsonObject
    {
        ["path"] = d.Path, ["id"] = d.Id, ["collection"] = d.Collection,
        ["createTime"] = d.CreateTime.ToString("O"), ["updateTime"] = d.UpdateTime.ToString("O"), ["fields"] = Fields(d.Fields),
    };

    public static JsonObject Fields(IReadOnlyDictionary<string, Value> fields) =>
        new(fields.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, Of(kv.Value))));

    public static JsonNode? Of(Value v) => v switch
    {
        NullValue => null,
        BooleanValue b => b.Value,
        IntegerValue i => i.Value,
        DoubleValue d => double.IsFinite(d.Value) ? d.Value : d.Value.ToString(),
        StringValue s => s.Value,
        TimestampValue t => t.Value.ToString("O"),
        BytesValue b => Convert.ToBase64String(b.Value),
        ReferenceValue r => r.Path,
        GeoPointValue g => new JsonObject { ["latitude"] = g.Latitude, ["longitude"] = g.Longitude },
        ArrayValue a => new JsonArray([.. a.Values.Select(Of)]),
        MapValue m => Fields(m.Fields),
        _ => v.ToString(),
    };
}
