namespace WincheDb.Core.Infrastructure;

/// <summary>
/// Represents parsed path components.
/// </summary>
public readonly record struct PathInfo(string Collection, string? Id);

public static class DocumentPathParser
{
    /// <summary>
    /// Parses the specified path string into a collection segment and an optional identifier segment.
    /// </summary>
    public static PathInfo ParsePath(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
            throw new ArgumentException("Path cannot be null or empty.", nameof(fullPath));

        var segments = fullPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
            return new PathInfo(string.Empty, null);

        bool hasId = int.IsEvenInteger(segments.Length);

        var collection = string.Join('/', segments.Take(hasId ? segments.Length - 1 : segments.Length));
        var id = hasId ? segments.Last() : null;

        return new PathInfo(collection, id);
    }

    public static bool IsValidDocumentPath(string path, out string? error)
    {
        var slashCount = path.Count(c => c == '/');
        if (!int.IsOddInteger(slashCount))
        {
            error = "Invalid path to document. Expected format is {collection/document/collection/...}";
            return false;
        }

        error = null;
        return true;
    }

    public static bool IsValidCollectionPath(string path, out string? error)
    {
        var slashCount = path.Count(c => c == '/');
        if (!int.IsEvenInteger(slashCount))
        {
            error = "Invalid path to collection. Expected format is {collection/document/collection/...}";
            return false;
        }

        error = null;
        return true;
    }
}