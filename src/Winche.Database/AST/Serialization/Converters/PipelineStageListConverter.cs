using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Winche.Database.AST.Models;
using Winche.Database.AST.Serialization.Parsers;

namespace Winche.Database.AST.Serialization.Converters;

public sealed class PipelineStageListConverter : JsonConverter<List<PipelineStage>>
{
    public override List<PipelineStage> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader);
        return node is JsonArray arr ? PipelineStageParser.ParseArray(arr) : [];
    }

    public override void Write(Utf8JsonWriter writer, List<PipelineStage> value, JsonSerializerOptions options)
        => throw new NotSupportedException();
}
