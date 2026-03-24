using System.Collections.Immutable;
using System.Text.Json.Nodes;
using WincheDb.Core.Ast;
using WincheDb.Core.Models;

namespace WincheDb.DocumentStore.Abstraction;

public enum AccessOperation {
    Get,
    Set,
    Update,
    Delete,
    Query,
}

public sealed record AccessContext
{
    public required AccessOperation Operation { get; init; }
    public IReadOnlyDictionary<string, object?> Claims { get; init; } = ImmutableDictionary<string, object?>.Empty;
    public string? Path { get; init; }
    public Query? Query { get; init; }
    public JsonObject? IncomingData { get; init; }
    public Func<CancellationToken, Task<Document?>>? GetExistingDocument { get; init; }
}
