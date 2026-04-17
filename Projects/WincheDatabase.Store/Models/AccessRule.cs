namespace WincheDatabase.Store.Models;

public sealed record AccessRule
{
    public string? Path { get; init; }
    public IReadOnlySet<AccessOperation>? Operations { get; init; }
    public required Func<AccessContext, CancellationToken, Task<bool>> Evaluate { get; init; }
}
