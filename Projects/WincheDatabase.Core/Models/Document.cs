using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace WincheDatabase.Core.Models
{
    public sealed record Document
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("collection")]
        public required string Collection { get; init; }

        [JsonPropertyName("path")]
        public required string Path { get; init; }

        [JsonPropertyName("createdAt")]
        public required DateTime CreatedAt { get; init; }

        [JsonPropertyName("updatedAt")]
        public required DateTime UpdatedAt { get; init; }

        [JsonPropertyName("data")]
        public required JsonObject Data { get; init; } = [];

        [JsonPropertyName("version")]
        public required long Version { get; init; }
    }
}
