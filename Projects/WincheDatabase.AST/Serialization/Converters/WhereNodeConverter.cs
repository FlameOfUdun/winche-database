using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WincheDatabase.AST.Serialization.Parsers;
using WincheDatabase.AST.Models;

namespace WincheDatabase.AST.Serialization.Converters;

public sealed class WhereNodeConverter : JsonConverter<WhereNode?>
{
    public override WhereNode? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader);
        return node is JsonObject obj ? WhereNodeParser.Parse(obj) : null;
    }

    public override void Write(Utf8JsonWriter writer, WhereNode? value, JsonSerializerOptions options)
        => throw new NotSupportedException();
}