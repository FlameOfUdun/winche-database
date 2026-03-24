using System.Text.Json.Nodes;

namespace WincheDb.DocumentStore.Models;

public enum DocumentChangeType
{
    Added,
    Modified,
    Removed,
}

public record DocumentChange
{
    public required DocumentChangeType Type { get; init; }
    public required string Id { get; init; }
    public required string Collection { get; init; }
    public required string Path { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime UpdatedAt { get; init; }
    public JsonObject? Data { get; init; }
    public required long Version { get; init; }
}
