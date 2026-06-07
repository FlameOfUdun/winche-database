using System.Text.Json.Serialization;
using Winche.Database.Documents;

namespace Winche.Database.Models
{
    public sealed record CommitResult
    {
        [JsonPropertyName("documents")]
        public List<Document?> Documents { get; init; } = [];
    }
}
