using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Winche.Database.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DocumentChangeType
{
    [JsonPropertyName("added")]
    Added,
    [JsonPropertyName("modified")]
    Modified,
    [JsonPropertyName("removed")]
    Removed,
}

public record DocumentChange
{
    [JsonPropertyName("type")]
    public required DocumentChangeType Type { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("collection")]
    public required string Collection { get; init; }

    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("created_at")]
    public required DateTime CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public required DateTime UpdatedAt { get; init; }

    [JsonPropertyName("data")]
    public JsonObject? Data { get; init; }

    [JsonPropertyName("version")]
    public required long Version { get; init; }
}
