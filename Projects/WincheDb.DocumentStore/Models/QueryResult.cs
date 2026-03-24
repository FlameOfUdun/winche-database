using WincheDb.Core.Models;

namespace WincheDb.DocumentStore.Models;

/// <summary>
/// Result of a query operation containing full document data.
/// Used for returning query results to clients.
/// </summary>
public record QueryResult
{
    public List<Document> Documents { get; init; } = [];
    public bool HasMore { get; init; }
}
