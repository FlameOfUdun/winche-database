using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WincheDatabase.AST.Models;
using WincheDatabase.AST.Serialization.Parsers;

namespace WincheDatabase.AST.Serialization.Converters;

public sealed class SortNodeListConverter : JsonConverter<List<SortNode>>
{
    public override List<SortNode> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader);
        return node is JsonArray arr ? SortNodeParser.ParseArray(arr) : [];
    }

    public override void Write(Utf8JsonWriter writer, List<SortNode> value, JsonSerializerOptions options)
        => throw new NotSupportedException();
}