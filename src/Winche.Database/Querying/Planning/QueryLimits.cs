namespace Winche.Database.Querying.Planning;

/// <summary>
/// Configurable ceilings on request shape for the query/read path. Independent of authorization —
/// these bound how much work a single request can force the planner, SQL compiler, and database
/// to do, regardless of who is allowed to issue it. Loosen by setting larger values.
/// </summary>
public sealed record QueryLimits
{
    /// <summary>Ceiling on <c>limit</c> and <c>limitToLast</c>. Default 10,000.</summary>
    public int MaxLimit { get; init; } = 10_000;

    /// <summary>Ceiling on <c>offset</c>. Default 10,000.</summary>
    public int MaxOffset { get; init; } = 10_000;

    /// <summary>
    /// Ceiling on the number of explicit <c>orderBy</c> keys (before the engine's implicit
    /// <c>__name__</c> tiebreaker). Bounds the cursor-matching and ordering SQL, both of which
    /// grow with the sort-key count. Default 32.
    /// </summary>
    public int MaxOrderByKeys { get; init; } = 32;

    /// <summary>Ceiling on the number of dotted segments in any field path referenced by the query. Default 32.</summary>
    public int MaxFieldPathSegments { get; init; } = 32;

    /// <summary>Ceiling on the character length of any single field-path segment. Default 1,500.</summary>
    public int MaxFieldPathSegmentLength { get; init; } = 1_500;

    /// <summary>
    /// Ceiling on the element count of an <c>in</c>/<c>notIn</c>/<c>arrayContainsAny</c>/
    /// <c>arrayContainsAll</c> operand array — each element compiles to its own SQL fragment and
    /// parameter. Default 100.
    /// </summary>
    public int MaxArrayOperandCount { get; init; } = 100;

    /// <summary>Ceiling on the number of document paths in a <c>:batchGet</c>/<c>doc.getAll</c> request. Default 500 (matches the write-batch cap).</summary>
    public int MaxBatchGetSize { get; init; } = 500;
}
