using System.Text.Json.Serialization;
using WincheDatabase.Core.Models;

namespace WincheDatabase.Store.Models;

public record QueryResult
{
    [JsonPropertyName("documents")]
    public List<Document> Documents { get; init; } = [];

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; init; }
}
