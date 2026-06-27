using System.Text.Json.Nodes;
using Winche.Database.Documents;
using Winche.Database.Runtime;

namespace Winche.Database.Querying;

/// <summary>Parses the wire <c>[{kind, alias, field?}]</c> aggregation array into <see cref="Aggregation"/>s.
/// Throws <see cref="RuntimeException"/> with <see cref="RuntimeStatus.InvalidArgument"/> on malformed input.</summary>
public static class AggregationParser
{
    public static IReadOnlyList<Aggregation> Parse(JsonNode? aggregations)
    {
        if (aggregations is null)
            throw new RuntimeException(RuntimeStatus.InvalidArgument, "'aggregations' is required");
        if (aggregations is not JsonArray arr)
            throw new RuntimeException(RuntimeStatus.InvalidArgument, "'aggregations' must be an array");
        var list = new List<Aggregation>(arr.Count);
        foreach (var el in arr)
        {
            if (el is not JsonObject o)
                throw new RuntimeException(RuntimeStatus.InvalidArgument, "each aggregation must be an object");
            var kind = (string?)o["kind"] switch
            {
                "count" => AggregateKind.Count,
                "sum" => AggregateKind.Sum,
                "average" => AggregateKind.Average,
                _ => throw new RuntimeException(RuntimeStatus.InvalidArgument, "aggregation 'kind' must be count|sum|average"),
            };
            var alias = o["alias"] switch
            {
                null => throw new RuntimeException(RuntimeStatus.InvalidArgument, "aggregation 'alias' is required"),
                JsonValue av when av.TryGetValue<string>(out var a) => a,
                _ => throw new RuntimeException(RuntimeStatus.InvalidArgument, "aggregation 'alias' must be a string"),
            };
            var fieldStr = (string?)o["field"];
            FieldPath? field;
            try { field = fieldStr is null ? null : FieldPath.Parse(fieldStr); }
            catch (ArgumentException ex) { throw new RuntimeException(RuntimeStatus.InvalidArgument, ex.Message); }
            list.Add(new Aggregation(kind, alias, field));
        }
        return list;
    }
}
