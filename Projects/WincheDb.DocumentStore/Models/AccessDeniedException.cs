namespace WincheDb.DocumentStore.Models;

public sealed class AccessDeniedException(AccessOperation operation, string? path = null)
    : Exception($"Access denied: {operation} on '{path ?? "resource"}'");
