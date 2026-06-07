using System.Text.Json.Serialization;
using Winche.Database.Values;

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

public sealed record DocumentChange
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
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; init; }

    [JsonPropertyName("version")]
    public long Version { get; init; }

    /// <summary>Typed fields, fetched in-process by ChangeProcessor. Never on the notify payload.</summary>
    [JsonIgnore] public IReadOnlyDictionary<string, Value>? Fields { get; init; }
}
