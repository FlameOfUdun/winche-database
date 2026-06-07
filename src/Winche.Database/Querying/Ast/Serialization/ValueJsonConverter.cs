using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Winche.Database.Values;

namespace Winche.Database.Querying.Ast.Serialization;

/// <summary>System.Text.Json bridge over ValueSerializer (attribute-based, no options plumbing).</summary>
public sealed class ValueJsonConverter : JsonConverter<Value>
{
    public override Value Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader)
            ?? throw new JsonException("Value cannot be null");
        try { return ValueSerializer.Read(node); }
        catch (WireFormatException ex) { throw new JsonException(ex.Message); }
    }

    public override void Write(Utf8JsonWriter writer, Value value, JsonSerializerOptions options) =>
        ValueSerializer.Write(value).WriteTo(writer);
}
