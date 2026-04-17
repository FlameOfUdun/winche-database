using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace WincheDatabase.Store.Models
{
    public sealed record MutationBatch
    {
        [Required]
        [JsonPropertyName("path")]
        public required string Path { get; init; }

        [Required]
        [JsonPropertyName("mutations")]
        public required List<Mutation> Mutations { get; init; }
    }
}
