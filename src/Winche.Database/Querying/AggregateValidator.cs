using Winche.Database.Runtime;

namespace Winche.Database.Querying;

/// <summary>Shape validation for an aggregation list. All violations are INVALID_ARGUMENT.</summary>
public static class AggregateValidator
{
    public const int MaxAggregations = 5;

    public static void Validate(IReadOnlyList<Aggregation> aggregations)
    {
        if (aggregations.Count == 0)
            throw Invalid("At least one aggregation is required.");
        if (aggregations.Count > MaxAggregations)
            throw Invalid($"At most {MaxAggregations} aggregations are allowed, got {aggregations.Count}.");

        var aliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in aggregations)
        {
            if (string.IsNullOrEmpty(a.Alias))
                throw Invalid("Each aggregation requires a non-empty alias.");
            if (!aliases.Add(a.Alias))
                throw Invalid($"Duplicate aggregation alias '{a.Alias}'.");

            if (a.Kind == AggregateKind.Count)
            {
                if (a.Field is not null)
                    throw Invalid("count() does not take a field.");
            }
            else
            {
                if (a.Field is null)
                    throw Invalid($"{a.Kind.ToString().ToLowerInvariant()}() requires a field.");
                if (a.Field.Segments is [ "__name__" ])
                    throw Invalid($"{a.Kind.ToString().ToLowerInvariant()}() cannot aggregate over __name__.");
            }
        }
    }

    private static RuntimeException Invalid(string message) => new(RuntimeStatus.InvalidArgument, message);
}
