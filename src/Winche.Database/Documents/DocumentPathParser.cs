namespace Winche.Database.Documents;

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

    /// <summary>True when the collection path contains a wildcard segment ('*').</summary>
    public static bool IsCollectionPattern(string path) => path.Contains('*');

    /// <summary>
    /// Anchored POSIX regex for a wildcard collection pattern: each '*' segment → [^/]+,
    /// literal segments verbatim (validated to a regex-safe charset by IsValidIndexPath).
    /// </summary>
    public static string CollectionPatternRegex(string path) =>
        "^" + string.Join('/', path.Split('/').Select(s => s == "*" ? "[^/]+" : s)) + "$";

    /// <summary>
    /// Validates an index Path (exact or wildcard pattern) against the strict grammar.
    /// Returns false + a specific error on violation (the caller throws InvalidPathPatternException).
    /// </summary>
    public static bool IsValidIndexPath(string path, out string? error)
    {
        if (string.IsNullOrEmpty(path)) { error = "path must be non-empty"; return false; }

        var segs = path.Split('/');
        if (segs.Length % 2 == 0) { error = "a collection path must have an odd number of segments"; return false; }

        for (var i = 0; i < segs.Length; i++)
        {
            var seg = segs[i];
            if (seg.Length == 0) { error = "segments must be non-empty (no leading/trailing or doubled '/')"; return false; }
            if (seg == "*")
            {
                if (i % 2 == 0) { error = "'*' is only allowed at document-id positions, not collection-name positions"; return false; }
            }
            else if (seg.Contains('*')) { error = $"segment '{seg}' mixes a wildcard with text; '*' must be a whole segment"; return false; }
            else if (!seg.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) { error = $"segment '{seg}' must match [A-Za-z0-9_-]+"; return false; }
        }

        error = null;
        return true;
    }
}
