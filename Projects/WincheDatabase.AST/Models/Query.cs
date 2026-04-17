using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using WincheDatabase.AST.Serialization.Converters;

namespace WincheDatabase.AST.Models
{
    public record Query
    {
        [Required]
        [JsonPropertyName("collection")]
        public required string Collection { get; set; }

        [JsonPropertyName("where")]
        [JsonConverter(typeof(WhereNodeConverter))]
        public WhereNode? Where { get; set; }

        [JsonPropertyName("orderBy")]
        [JsonConverter(typeof(SortNodeListConverter))]
        public List<SortNode> OrderBy { get; set; } = [];

        [JsonPropertyName("limit")]
        public int Limit { get; set; } = 100;

        [JsonPropertyName("startAfter")]
        public List<object?> StartAfter { get; set; } = [];

        [JsonPropertyName("startAt")]
        public List<object?> StartAt { get; set; } = [];

        [JsonPropertyName("endBefore")]
        public List<object?> EndBefore { get; set; } = [];

        [JsonPropertyName("endAt")]
        public List<object?> EndAt { get; set; } = [];
    }
}
