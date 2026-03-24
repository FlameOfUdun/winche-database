namespace WincheDb.DocumentStore.Abstraction;

public sealed record AccessRule
{
    public string? Path { get; init; }
    public IReadOnlySet<AccessOperation>? Operations { get; init; }
    public required Func<RuleAccessContext, CancellationToken, Task<bool>> Evaluate { get; init; }
}
