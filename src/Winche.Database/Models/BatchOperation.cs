using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Winche.Database.Models
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum BatchOperationType
    {
        [JsonPropertyName("set")]
        Set,
        [JsonPropertyName("update")]
        Update,
        [JsonPropertyName("delete")]
        Delete
    }

    public record BatchOperation
    {
        [Required]
        [JsonPropertyName("type")]
        public required BatchOperationType Type { get; init; }

        [Required]
        [JsonPropertyName("path")]
        public required string Path { get; init; }

        [JsonPropertyName("data")]
        public JsonObject? Data { get; init; }
    }
}
