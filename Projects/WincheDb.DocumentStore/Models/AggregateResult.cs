using System.Text.Json.Nodes;

namespace WincheDb.DocumentStore.Models;

public sealed record AggregateResult(
    IReadOnlyList<IReadOnlyDictionary<string, JsonNode?>> Rows
)
{
    public int Count => Rows.Count;
    public bool IsEmpty => Rows.Count == 0;
    public IReadOnlyDictionary<string, JsonNode?> this[int index] => Rows[index];
}