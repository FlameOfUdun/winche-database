using System.Text.Json.Serialization;
using WincheDatabase.Core.Models;

namespace WincheDatabase.Store.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QueryChangeType
{
    [JsonPropertyName("added")]
    Added,

    [JsonPropertyName("modified")]
    Modified,

    [JsonPropertyName("removed")]
    Removed,
}

public record QueryChange
{
    [JsonPropertyName("type")]
    public QueryChangeType Type { get; set; }

    [JsonPropertyName("document")]
    public Document? Document { get; init; }

    [JsonPropertyName("documentId")]
    public string? DocumentId { get; init; }
}
