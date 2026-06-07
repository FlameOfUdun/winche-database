using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Winche.Database.Values;

namespace Winche.Database.Querying.Ast.Serialization;

/// <summary>A typed field map ({"age":{"integerValue":"30"},…}) on the wire.</summary>
public sealed class FieldsJsonConverter : JsonConverter<IReadOnlyDictionary<string, Value>>
{
    public override IReadOnlyDictionary<string, Value> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (JsonNode.Parse(ref reader) is not JsonObject obj)
            throw new JsonException("fields must be a JSON object");
        try { return ValueSerializer.ReadFields(obj); }
        catch (WireFormatException ex) { throw new JsonException(ex.Message); }
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyDictionary<string, Value> value, JsonSerializerOptions options) =>
        ValueSerializer.WriteFields(value).WriteTo(writer);
}
