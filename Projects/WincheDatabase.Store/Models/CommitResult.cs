using System.Text.Json.Serialization;
using WincheDatabase.Core.Models;

namespace WincheDatabase.Store.Models
{
    public sealed record CommitResult
    {
        [JsonPropertyName("documents")]
        public List<Document?> Documents { get; init; } = [];
    }
}
