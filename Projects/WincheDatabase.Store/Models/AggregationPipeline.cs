using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using WincheDatabase.AST.Models;
using WincheDatabase.AST.Serialization.Converters;

namespace WincheDatabase.Store.Models
{
    public sealed record AggregationPipeline
    {
        [Required]
        [JsonPropertyName("stages")]
        [JsonConverter(typeof(PipelineStageListConverter))]
        public required List<PipelineStage> Stages { get; init; } = [];
    }
}
