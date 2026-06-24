namespace Winche.Database.Runtime.Writes;

/// <summary>
/// Configurable write-validation limits applied to the resulting document (the default limits).
/// Loosen by setting larger values; disable reserved-name checks with RejectReservedFieldNames=false.
/// </summary>
public sealed record WriteLimits
{
    /// <summary>Max document size in bytes (the published byte-budget formula). Default 1 MiB.</summary>
    public long MaxDocumentSizeBytes { get; init; } = 1_048_576;

    /// <summary>Max map/array nesting depth. Default 20.</summary>
    public int MaxDepth { get; init; } = 20;

    /// <summary>Reject field names matching the reserved <c>__*__</c> pattern. Default true.</summary>
    public bool RejectReservedFieldNames { get; init; } = true;
}
