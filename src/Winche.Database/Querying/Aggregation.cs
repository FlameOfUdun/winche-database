using Winche.Database.Documents;
using Winche.Database.Values;

namespace Winche.Database.Querying;

public enum AggregateKind { Count, Sum, Average }

/// <summary>One aggregation over a query. Count has no field; Sum/Average require one.</summary>
public sealed record Aggregation(AggregateKind Kind, string Alias, FieldPath? Field = null)
{
    public static Aggregation Count(string alias) => new(AggregateKind.Count, alias);
    public static Aggregation Sum(string field, string alias) => new(AggregateKind.Sum, alias, FieldPath.Parse(field));
    public static Aggregation Average(string field, string alias) => new(AggregateKind.Average, alias, FieldPath.Parse(field));
}

/// <summary>Aggregation results keyed by alias (result of the aggregate() operation).</summary>
public sealed record AggregationResult(IReadOnlyDictionary<string, Value> Values);
