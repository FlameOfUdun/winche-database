using System.Text;

namespace Winche.Database.Documents;

/// <summary>
/// Represents parsed path components.
/// </summary>
public readonly record struct PathInfo(string Collection, string? Id, string CollectionId);

public static class DocumentPathParser
{
    /// <summary>Matches the reference document-database's per-segment id limit.</summary>
    private const int MaxSegmentBytes = 1500;

    /// <summary>
    /// Parses the specified path string into a collection segment and an optional identifier segment.
    /// </summary>
    public static PathInfo ParsePath(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
            throw new ArgumentException("Path cannot be null or empty.", nameof(fullPath));

        // RemoveEmptyEntries is safe here ONLY because callers validate with IsValid*Path first,
        // which rejects a leading/trailing/doubled '/' outright rather than normalizing it away.
        // Do not let this drift from the validators — an empty segment must never reach here silently.
        var segments = fullPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
            return new PathInfo(string.Empty, null, string.Empty);

        bool hasId = int.IsEvenInteger(segments.Length);

        var collection = string.Join('/', segments.Take(hasId ? segments.Length - 1 : segments.Length));
        var id = hasId ? segments.Last() : null;

        return new PathInfo(collection, id, CollectionIdOf(collection));
    }

    /// <summary>A document path: an even number of well-formed segments (collection/document/collection/document/...).</summary>
    public static bool IsValidDocumentPath(string path, out string? error) =>
        TryValidateShape(path, static n => int.IsEvenInteger(n),
            "Invalid path to document. Expected format is {collection/document/collection/...}", out error);

    /// <summary>A collection path: an odd number of well-formed segments (collection/document/collection/...).</summary>
    public static bool IsValidCollectionPath(string path, out string? error) =>
        TryValidateShape(path, static n => int.IsOddInteger(n),
            "Invalid path to collection. Expected format is {collection/document/collection/...}", out error);

    /// <summary>Either shape (document or collection) — used where callers accept both, e.g. cascade deletes.</summary>
    public static bool IsValidPath(string path, out string? error) =>
        TryValidateShape(path, static n => n >= 1,
            "Path must contain at least one segment.", out error);

    private static bool TryValidateShape(string path, Func<int, bool> segmentCountOk, string shapeError, out string? error)
    {
        if (string.IsNullOrEmpty(path))
        {
            error = "Path cannot be null or empty.";
            return false;
        }

        // Split WITHOUT RemoveEmptyEntries: a leading '/', a trailing '/', or a doubled '/' all
        // produce an empty segment here, which is rejected below instead of silently dropped —
        // that silent drop is exactly what let a non-canonical path masquerade as a canonical one.
        var segments = path.Split('/');
        foreach (var segment in segments)
        {
            if (!IsValidSegment(segment, out error))
                return false;
        }

        if (!segmentCountOk(segments.Length))
        {
            error = shapeError;
            return false;
        }

        error = null;
        return true;
    }

    private static bool IsValidSegment(string segment, out string? error)
    {
        if (segment.Length == 0)
        {
            error = "Path segments cannot be empty (no leading, trailing, or doubled '/').";
            return false;
        }
        if (segment is "." or "..")
        {
            error = "Path segments '.' and '..' are not allowed.";
            return false;
        }
        // Reserved namespace, matches the reference document-database's __*__ convention.
        if (segment.Length >= 4 && segment.StartsWith("__", StringComparison.Ordinal) && segment.EndsWith("__", StringComparison.Ordinal))
        {
            error = "Path segments matching '__*__' are reserved.";
            return false;
        }
        if (Encoding.UTF8.GetByteCount(segment) > MaxSegmentBytes)
        {
            error = $"Path segment exceeds the {MaxSegmentBytes}-byte limit.";
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
