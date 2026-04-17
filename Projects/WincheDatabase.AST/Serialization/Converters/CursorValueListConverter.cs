using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WincheDatabase.AST.Serialization.Parsers;

namespace WincheDatabase.AST.Serialization.Converters;

public sealed class CursorValueListConverter : JsonConverter<List<object?>>
{
    public override List<object?> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader);
        return node is JsonArray arr ? CursorValueParser.Parse(arr) : [];
    }

    public override void Write(Utf8JsonWriter writer, List<object?> value, JsonSerializerOptions options)
        => throw new NotSupportedException();
}