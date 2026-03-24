using System.Collections.Immutable;
using System.Text.Json.Nodes;
using WincheDb.Core.Ast;
using WincheDb.Core.Models;

namespace WincheDb.DocumentStore.Abstraction;

public sealed record RuleAccessContext
{
    public required AccessContext Context { get; init; }
    public IReadOnlyDictionary<string, string> PathParams { get; init; } = ImmutableDictionary<string, string>.Empty;
    public AccessOperation Operation => Context.Operation;
    public IReadOnlyDictionary<string, object?> Claims => Context.Claims;
    public string? Path => Context.Path;
    public Query? Query => Context.Query;
    public JsonObject? IncomingData => Context.IncomingData;
    public Func<CancellationToken, Task<Document?>>? GetExistingDocument => Context.GetExistingDocument;
}
