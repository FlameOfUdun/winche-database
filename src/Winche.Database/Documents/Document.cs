using System.Text.Json.Serialization;
using Winche.Database.Querying.Ast.Serialization;
using Winche.Database.Values;

namespace Winche.Database.Documents;

/// <summary>A typed document: metadata + a typed field map.</summary>
public sealed record Document
{
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("collection")] public required string Collection { get; init; }

    [JsonPropertyName("fields")]
    [JsonConverter(typeof(FieldsJsonConverter))]
    public required IReadOnlyDictionary<string, Value> Fields { get; init; }

    [JsonPropertyName("createTime")] public required DateTimeOffset CreateTime { get; init; }
    [JsonPropertyName("updateTime")] public required DateTimeOffset UpdateTime { get; init; }
    [JsonPropertyName("version")] public required long Version { get; init; }
}
