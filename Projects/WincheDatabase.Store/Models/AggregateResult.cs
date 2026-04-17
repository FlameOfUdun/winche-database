using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace WincheDatabase.Store.Models;

public sealed record AggregateResult(
    IReadOnlyList<IReadOnlyDictionary<string, JsonNode?>> Rows
)
{
    [JsonIgnore]
    public int Count => Rows.Count;

    [JsonIgnore]
    public bool IsEmpty => Rows.Count == 0;

    [JsonPropertyName("rows")]
    public IReadOnlyDictionary<string, JsonNode?> this[int index] => Rows[index];
}