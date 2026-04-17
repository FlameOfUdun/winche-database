using System.Collections.Immutable;
using System.Text.Json.Nodes;
using WincheDatabase.AST.Models;
using WincheDatabase.Core.Models;

namespace WincheDatabase.Store.Models;

public enum AccessOperation {
    Read,
    Write, 
    Delete,
}

public sealed record AccessContext
{
    public required AccessOperation Operation { get; init; }
    public IReadOnlyDictionary<string, object?> Claims { get; init; } = ImmutableDictionary<string, object?>.Empty;
    public IReadOnlyDictionary<string, string> Params { get; set; } = ImmutableDictionary<string, string>.Empty;
    public string? Path { get; init; }
    public Query? Query { get; init; }
    public JsonObject? IncomingData { get; init; }
    public Func<CancellationToken, Task<Document?>>? GetExistingDocument { get; init; }
}
