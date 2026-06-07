using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Winche.Database.Querying.Ast.Serialization;
using Winche.Database.Values;

namespace Winche.Database.Models
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

        [JsonPropertyName("fields")]
        [JsonConverter(typeof(FieldsJsonConverter))]
        public IReadOnlyDictionary<string, Value>? Fields { get; init; }

        [JsonPropertyName("baseVersion")]
        public long? BaseVersion { get; init; }
    }
}
