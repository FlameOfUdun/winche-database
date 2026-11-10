using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Matching;
using Winche.Database.Querying.Planning;
using Winche.Database.Runtime.ChangeFeed;
using Winche.Database.Values;

namespace Winche.Database.Runtime.Listening;

/// <summary>
/// Applies a change batch to a live query's current result without re-running it — only when that gives
/// exactly what the query would return. Filter and order use the engine's own evaluator and comparer
/// (<see cref="FilterEvaluator"/>, <see cref="ValueComparer"/>), which the SQL compiler mirrors.
/// </summary>
internal static class InMemoryQuery
{
    private static readonly FieldPath Name = FieldPath.Parse("__name__");

    /// <summary>
    /// The query's sort keys (with the __name__ tiebreaker) when its result can be maintained in memory;
    /// null when it always needs a re-query: a projection (the result isn't whole documents), an offset,
    /// limitToLast, or cursors (the window depends on documents outside the result).
    /// </summary>
    public static IReadOnlyList<SortKey>? SortKeysFor(Query query)
    {
        if (query.Select is { Count: > 0 } || query.Offset is not null || query.LimitToLast is not null
            || query.Start is not null || query.End is not null)
            return null;
        try
        {
            return Normalizer.Normalize(query).Nodes.OfType<SortNode>().Single().Keys;
        }
        catch (PlanValidationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The result after <paramref name="batch"/>, or null when it can't be worked out without the database:
    /// a limited result that was full and lost or changed a member (the next document isn't known here), or
    /// a document the filter or comparer can't evaluate.
    /// </summary>
    public static IReadOnlyList<Document>? TryApply(
        Query query, Filter? predicate, IReadOnlyList<SortKey> keys, IReadOnlyList<Document> current, ChangeBatch batch)
    {
        try
        {
            var result = new List<Document>(current);
            // With a limit and a full result, documents beyond the window exist that we don't hold.
            var full = query.Limit is { } limit && current.Count >= limit;

            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in batch.Records)
            {
                if (record.Collection != query.Collection || !paths.Add(record.Path)) continue;

                // The batch holds each changed document's current state; absent = it doesn't exist now.
                batch.Documents.TryGetValue(record.Path, out var doc);
                var matches = doc is not null && (predicate is null || FilterEvaluator.Matches(predicate, doc.Path, doc.Fields));
                var at = result.FindIndex(d => d.Path == record.Path);

                if (full)
                {
                    if (at >= 0) return null;                     // a member changed or left: the next one is unknown
                    if (!matches) continue;
                    // A newcomer counts only if it sorts inside the window; it pushes the last one out.
                    if (Compare(keys, doc!, result[^1]) > 0) continue;
                    Insert(keys, result, doc!);
                    result.RemoveAt(result.Count - 1);
                    continue;
                }

                // Not full: the result is every matching document, so the change applies exactly.
                if (at >= 0) result.RemoveAt(at);
                if (matches) Insert(keys, result, doc!);
            }

            if (query.Limit is { } n && result.Count > n) result.RemoveRange(n, result.Count - n);
            return result;
        }
        catch (Exception)
        {
            return null;   // anything unexpected (a regex timeout, an uncomparable value): let the database decide
        }
    }

    private static void Insert(IReadOnlyList<SortKey> keys, List<Document> result, Document doc)
    {
        int lo = 0, hi = result.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (Compare(keys, result[mid], doc) < 0) lo = mid + 1; else hi = mid;
        }
        result.Insert(lo, doc);
    }

    private static int Compare(IReadOnlyList<SortKey> keys, Document a, Document b)
    {
        foreach (var key in keys)
        {
            var c = ValueComparer.Instance.Compare(KeyOf(key, a), KeyOf(key, b));
            if (c != 0) return key.Direction == SortDirection.Desc ? -c : c;
        }
        return 0;
    }

    private static Value KeyOf(SortKey key, Document doc) =>
        key.Field.Equals(Name)
            ? new ReferenceValue(doc.Path)
            // orderBy fields carry an implicit exists filter, so every result document has them.
            : FilterEvaluator.ResolveField(key.Field, doc.Fields)
              ?? throw new InvalidOperationException($"'{doc.Path}' has no '{key.Field}' to sort by");
}
