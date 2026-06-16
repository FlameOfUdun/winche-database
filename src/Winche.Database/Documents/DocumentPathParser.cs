namespace Winche.Database.Documents;

/// <summary>
/// Represents parsed path components.
/// </summary>
public readonly record struct PathInfo(string Collection, string? Id, string CollectionId);

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
            return new PathInfo(string.Empty, null, string.Empty);

        bool hasId = int.IsEvenInteger(segments.Length);

        var collection = string.Join('/', segments.Take(hasId ? segments.Length - 1 : segments.Length));
        var id = hasId ? segments.Last() : null;

        return new PathInfo(collection, id, CollectionIdOf(collection));
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

    public static bool IsValidPath(string path, out string? error)
    {
        if (string.IsNullOrEmpty(path))
        {
            error = "Path cannot be null or empty.";
            return false;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            error = "Path must contain at least one segment.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>The collection's id: the last segment of a collection path. "userData/alice/sessionHistory" → "sessionHistory".</summary>
    public static string CollectionIdOf(string collectionPath)
    {
        var segments = collectionPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? string.Empty : segments[^1];
    }

    /// <summary>Validates a collection ID: a single segment matching ^[A-Za-z0-9_-]+$ (no slashes, no '*').</summary>
    public static bool IsValidCollectionId(string id, out string? error)
    {
        if (string.IsNullOrEmpty(id)) { error = "collection id must be non-empty"; return false; }
        if (!id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
        {
            error = $"collection id '{id}' must match [A-Za-z0-9_-]+ (single segment, no '/' or '*')";
            return false;
        }
        error = null;
        return true;
    }

}
