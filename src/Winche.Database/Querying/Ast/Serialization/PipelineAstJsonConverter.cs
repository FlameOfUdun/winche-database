using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Winche.Database.Querying.Ast.Serialization;

public sealed class PipelineAstJsonConverter : JsonConverter<PipelineAst>
{
    public override PipelineAst Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (JsonNode.Parse(ref reader) is not JsonObject obj)
            throw new JsonException("Pipeline must be a JSON object");
        try { return PipelineParser.Parse(obj); }
        catch (QueryParseException ex) { throw new JsonException(ex.Message); }
    }

    public override void Write(Utf8JsonWriter writer, PipelineAst value, JsonSerializerOptions options) =>
        PipelineAstWriter.Write(value).WriteTo(writer);
}
