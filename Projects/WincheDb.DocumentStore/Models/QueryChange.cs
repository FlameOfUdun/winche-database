using WincheDb.Core.Models;

namespace WincheDb.DocumentStore.Models;

public enum QueryChangeType
{
    Added,
    Modified,
    Removed,
}

public record QueryChange
{
    public QueryChangeType Type { get; set; }
    public Document? Document { get; init; }
    public string? DocumentId { get; init; }
}
