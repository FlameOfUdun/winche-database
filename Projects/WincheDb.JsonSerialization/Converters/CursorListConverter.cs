using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WincheDb.JsonSerialization.Parsers;

namespace WincheDb.JsonSerialization.Converters;

public sealed class CursorListConverter : JsonConverter<List<object?>>
{
    public override List<object?> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader);
        return node is JsonArray arr ? CursorParser.Parse(arr) : [];
    }

    public override void Write(Utf8JsonWriter writer, List<object?> value, JsonSerializerOptions options)
        => throw new NotSupportedException();
}