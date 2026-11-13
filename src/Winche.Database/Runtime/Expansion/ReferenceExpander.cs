using Winche.Database.Documents;
using Winche.Values;

namespace Winche.Database.Runtime.Expansion;

/// <summary>
/// Which references to follow: dotted paths (<c>patient</c>, <c>patient.doctor</c>, <c>team</c>, <c>visits.doctor</c>).
/// Each segment names a field; maps are walked into, lists apply the rest to every item, and a reference applies the rest
/// to the document it points to. So <c>patient.doctor</c> follows <c>patient</c>, then <c>doctor</c> in that document.
/// </summary>
public sealed record ExpandRequest
{
    /// <summary>The most paths one request may give.</summary>
    public const int MaxPaths = 10;
    /// <summary>The most references followed one after another (<c>a.b.c.d</c> is four).</summary>
    public const int MaxDepth = 4;
    /// <summary>The most documents one response includes; past it, references stay pointers.</summary>
    public const int MaxDocuments = 100;

    public IReadOnlyList<string[]> Paths { get; }

    private ExpandRequest(IReadOnlyList<string[]> paths) => Paths = paths;

    /// <summary>The request for <paramref name="paths"/>, or null when there are none; throws for a malformed one.</summary>
    public static ExpandRequest? Of(IEnumerable<string>? paths)
    {
        var list = (paths ?? []).Select(p => p?.Trim() ?? "").Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (list.Count == 0) return null;
        if (list.Count > MaxPaths)
            throw new RuntimeException(RuntimeStatus.InvalidArgument, $"expand takes at most {MaxPaths} paths.");
        var parsed = new List<string[]>();
        foreach (var p in list)
        {
            var segments = p.Split('.');
            if (segments.Any(s => s.Length == 0 || s.Length > 1500))
                throw new RuntimeException(RuntimeStatus.InvalidArgument, $"'{p}' isn't a field path to expand (fields separated by dots).");
            parsed.Add(segments);
        }
        return new ExpandRequest(parsed);
    }

    /// <summary>For query strings: <c>expand=patient,patient.doctor</c>.</summary>
    public static ExpandRequest? Parse(string? commaSeparated) =>
        string.IsNullOrWhiteSpace(commaSeparated) ? null : Of(commaSeparated.Split(','));
}

/// <summary>What expanding found: the readable documents referenced (by path), and every path referenced (readable or not).</summary>
public sealed record Expansion(IReadOnlyDictionary<string, Document> Included, IReadOnlySet<string> Referenced, bool Truncated)
{
    public static readonly Expansion Empty = new(new Dictionary<string, Document>(), new HashSet<string>(), false);
}

/// <summary>
/// Follows references in documents: one batch read per level, each document read as the caller (a document they may not
/// get, or that doesn't exist, is left out, so its reference stays a pointer). A document referenced twice, or a cycle,
/// is read once.
/// </summary>
public static class ReferenceExpander
{
    public static async Task<Expansion> ExpandAsync(IDocumentDatabase db, IEnumerable<Document?> roots, ExpandRequest? request, CancellationToken ct = default)
    {
        if (request is null) return Expansion.Empty;
        var included = new Dictionary<string, Document>(StringComparer.Ordinal);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        var read = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false;

        // (document path, what to follow inside it), level by level.
        var level = new List<(string Path, string[] Then)>();
        foreach (var root in roots)
            if (root is not null)
                foreach (var path in request.Paths)
                    Walk(new MapValue(root.Fields), path, 0, level);

        for (var depth = 1; level.Count > 0; depth++)
        {
            if (depth > ExpandRequest.MaxDepth) { truncated = true; break; }
            var toRead = new List<string>();
            foreach (var (path, _) in level)
            {
                referenced.Add(path);
                if (read.Contains(path) || toRead.Contains(path)) continue;
                if (read.Count + toRead.Count >= ExpandRequest.MaxDocuments) { truncated = true; continue; }
                toRead.Add(path);
            }
            if (toRead.Count > 0)
            {
                var docs = await db.GetReadableAsync(toRead, ct);
                for (var i = 0; i < toRead.Count; i++)
                {
                    read.Add(toRead[i]);
                    if (docs[i] is { } doc) included[toRead[i]] = doc;
                }
            }
            var next = new List<(string, string[])>();
            foreach (var (path, rest) in level)
                if (rest.Length > 0 && included.TryGetValue(path, out var doc))
                    Walk(new MapValue(doc.Fields), rest, 0, next);
            level = next;
        }
        return new Expansion(included, referenced, truncated);
    }

    /// <summary>
    /// Collects the references reached by following <paramref name="segments"/> from <paramref name="i"/> in
    /// <paramref name="value"/>: maps are walked into by name, lists apply the same step to each item, and a reference is
    /// found with whatever of the path is left (to follow inside the document it points to).
    /// </summary>
    private static void Walk(Value value, string[] segments, int i, List<(string Path, string[] Then)> found)
    {
        switch (value)
        {
            case ReferenceValue r:
                if (Target(r) is { } p) found.Add((p, segments[i..]));
                break;
            case ArrayValue a:
                foreach (var item in a.Values) Walk(item, segments, i, found);
                break;
            case MapValue m when i < segments.Length:
                if (m.Fields.TryGetValue(segments[i], out var child)) Walk(child, segments, i + 1, found);
                break;
        }
    }

    private static string? Target(ReferenceValue r)
    {
        var path = r.Path.Trim('/');
        return DocumentPathParser.IsValidDocumentPath(path, out _) ? path : null;
    }
}
