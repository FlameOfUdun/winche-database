using System.Text.Json;
using System.Text.Json.Serialization;
using Winche.Database.Documents;
using Winche.Database.Values;

namespace Winche.Database.Runtime.Writes;

/// <summary>
/// Converts IReadOnlyDictionary&lt;FieldPath, Value&gt; to/from a JSON object
/// where keys are the dotted field-path strings (e.g. "address.city").
/// Used on WriteResult.TransformResults so the wire format is a JSON object
/// keyed by dotted path rather than an opaque object.
/// </summary>
public sealed class FieldPathValueDictionaryJsonConverter
    : JsonConverter<IReadOnlyDictionary<FieldPath, Value>?>
{
    public override IReadOnlyDictionary<FieldPath, Value>? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected object for FieldPath dictionary.");

        var dict = new Dictionary<FieldPath, Value>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected property name.");
            var key = FieldPath.Parse(reader.GetString()!);
            reader.Read();
            var value = JsonSerializer.Deserialize<Value>(ref reader, options)!;
            dict[key] = value;
        }
        return dict;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyDictionary<FieldPath, Value>? value,
        JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        writer.WriteStartObject();
        foreach (var (path, val) in value)
        {
            writer.WritePropertyName(path.ToString());
            // Serialize as the abstract Value type so the type-level [JsonConverter(typeof(ValueJsonConverter))]
            // on Value is found; using the concrete type would miss the attribute on the base.
            JsonSerializer.Serialize(writer, val, typeof(Value), options);
        }
        writer.WriteEndObject();
    }
}
