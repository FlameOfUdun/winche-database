using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace WincheDatabase.Store.Models
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum MutationType
    {
        [JsonPropertyName("set")]
        Set,
        [JsonPropertyName("update")]
        Update,
        [JsonPropertyName("delete")]
        Delete,
    }

    public record Mutation
    {
        [Required]
        [JsonPropertyName("type")]
        public required MutationType Type { get; init; }

        [JsonPropertyName("data")]
        public JsonObject? Data { get; init; }

        [JsonPropertyName("baseVersion")]
        public long? BaseVersion { get; init; }
    }
}
