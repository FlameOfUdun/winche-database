using System.Text.Json.Serialization;
using Winche.Database.Documents;

namespace Winche.Database.Querying;

public sealed record QueryResult(
    [property: JsonPropertyName("documents")] IReadOnlyList<Document> Documents,
    [property: JsonPropertyName("hasMore")] bool HasMore);
