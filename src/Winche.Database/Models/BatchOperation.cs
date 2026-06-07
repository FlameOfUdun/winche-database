using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Winche.Database.Querying.Ast.Serialization;
using Winche.Database.Values;

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

    public sealed record BatchOperation
    {
        [Required]
        [JsonPropertyName("type")]
        public required BatchOperationType Type { get; init; }

        [Required]
        [JsonPropertyName("path")]
        public required string Path { get; init; }

        [JsonPropertyName("fields")]
        [JsonConverter(typeof(FieldsJsonConverter))]
        public IReadOnlyDictionary<string, Value>? Fields { get; init; }
    }
}
