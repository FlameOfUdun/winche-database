namespace Winche.Database.Authorization;

/// <summary>
/// Thrown by <see cref="RuleGuardedDocumentDatabase"/> when the Winche.Rules engine denies
/// an operation. Carries the document or collection <paramref name="path"/> and a short
/// <paramref name="operation"/> descriptor ("get", "create", "update", "delete", "list").
/// </summary>
/// <param name="path">Document path or collection path that was denied.</param>
/// <param name="operation">Operation descriptor: "get", "create", "update", "delete", or "list".</param>
public sealed class AccessDeniedException(string path, string operation)
    : Exception($"Access denied: {operation} on '{path}'")
{
    /// <summary>Document or collection path that was denied.</summary>
    public string Path { get; } = path;

    /// <summary>Operation descriptor: "get", "create", "update", "delete", or "list".</summary>
    public string Operation { get; } = operation;
}
