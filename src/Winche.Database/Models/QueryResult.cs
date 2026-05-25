using System.Text.Json.Serialization;
using Winche.Database.Core.Models;

namespace Winche.Database.Models;

public record QueryResult
{
    [JsonPropertyName("documents")]
    public List<Document> Documents { get; init; } = [];

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; init; }
}
