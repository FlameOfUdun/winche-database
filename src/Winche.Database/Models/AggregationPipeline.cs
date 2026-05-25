using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Winche.Database.AST.Models;
using Winche.Database.AST.Serialization.Converters;

namespace Winche.Database.Models
{
    public sealed record AggregationPipeline
    {
        [Required]
        [JsonPropertyName("stages")]
        [JsonConverter(typeof(PipelineStageListConverter))]
        public required List<PipelineStage> Stages { get; init; } = [];
    }
}
