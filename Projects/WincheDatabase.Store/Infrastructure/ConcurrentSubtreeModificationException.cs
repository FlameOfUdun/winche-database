namespace WincheDatabase.Store.Infrastructure;

public sealed class ConcurrentSubtreeModificationException : Exception
{
    public string RequestedPath { get; }
    public IReadOnlyList<string> UnauthorizedPaths { get; }

    public ConcurrentSubtreeModificationException(string requestedPath, IReadOnlyList<string> unauthorizedPaths)
        : base(BuildMessage(requestedPath, unauthorizedPaths))
    {
        RequestedPath = requestedPath;
        UnauthorizedPaths = unauthorizedPaths;
    }

    private static string BuildMessage(string requestedPath, IReadOnlyList<string> unauthorizedPaths)
    {
        return $"Delete of '{requestedPath}' aborted: {unauthorizedPaths.Count} path(s) were inserted into the subtree concurrently and could not be authorized. Retry the delete.";
    }
}
