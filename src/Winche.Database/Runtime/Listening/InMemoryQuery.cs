using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Matching;
using Winche.Database.Querying.Planning;
using Winche.Database.Runtime.ChangeFeed;
using Winche.Database.Values;

namespace Winche.Database.Runtime.Listening;

/// <summary>
/// The documents a live query holds so it can apply changes in memory: the first <c>limit + buffer</c>
/// matches in sort order (all of them when <see cref="Complete"/>), as whole documents.
/// </summary>
internal sealed record HeldWindow(List<Document> Documents, bool Complete);

/// <summary>
/// How a live query is kept up to date without re-running it — only in ways that give exactly what the
/// query would return. The filter, cursors and order use the engine's own evaluator and comparer
/// (<see cref="FilterEvaluator"/>, <see cref="ValueComparer"/>), which the SQL compiler mirrors.
///
/// The query holds the first <c>limit + buffer</c> matching documents, whole (a projection is applied only
/// when the result is emitted). A change applies to that prefix of the sorted matches; when members leave,
/// the spares move up, and only when fewer than <c>limit</c> are left (and more exist) is it re-run.
/// limitToLast is the same from the other end. An offset always re-runs: a change before the window
/// shifts it onto documents that aren't held.
/// </summary>
internal sealed class InMemoryQuery
{
    private static readonly FieldPath Name = FieldPath.Parse("__name__");

    private readonly Query _query;
    private readonly Filter? _predicate;
    private readonly IReadOnlyList<SortKey> _keys;   // in plan order (flipped for limitToLast)
    private readonly CursorRangeNode? _range;
    private readonly int _limit;
    private readonly bool _reverse;
    private readonly int _held;

    private InMemoryQuery(Query query, LogicalPlan plan, int buffer)
    {
        _query = query;
        _predicate = plan.Nodes.OfType<FilterNode>().FirstOrDefault()?.Predicate;
        _keys = plan.Nodes.OfType<SortNode>().Single().Keys;
        _range = plan.Nodes.OfType<CursorRangeNode>().FirstOrDefault();
        var page = plan.Nodes.OfType<PageNode>().Single();
        _limit = page.Limit;
        _reverse = page.ReverseResult;
        _held = page.Limit + Math.Max(0, buffer);
    }

    /// <summary>Null when the query must always be re-run (an offset, or a query the planner rejects).</summary>
    public static InMemoryQuery? For(Query query, int buffer)
    {
        if (query.Offset is > 0) return null;
        try
        {
            return new InMemoryQuery(query, Normalizer.Normalize(query), buffer);
        }
        catch (PlanValidationException)
        {
            return null;
        }
    }

    /// <summary>What to fetch from the database: whole documents, <c>limit + buffer</c> of them.</summary>
    public Query FetchQuery => _reverse
        ? _query with { Select = null, Offset = null, Limit = null, LimitToLast = _held }
        : _query with { Select = null, Offset = null, Limit = _held, LimitToLast = null };

    /// <summary>The held window from a <see cref="FetchQuery"/> result (which comes back in query order).</summary>
    public HeldWindow FromFetch(IReadOnlyList<Document> fetched)
    {
        var documents = fetched.ToList();
        if (_reverse) documents.Reverse();
        return new HeldWindow(documents, Complete: fetched.Count < _held);
    }

    /// <summary>The query's result: the first <c>limit</c> held documents, in query order, projected.</summary>
    public IReadOnlyList<Document> Result(HeldWindow held)
    {
        var window = held.Documents.Take(_limit).ToList();
        if (_reverse) window.Reverse();
        if (_query.Select is { Count: > 0 } select)
            window = [.. window.Select(d => d with { Fields = Projection.Apply(select, d.Fields) })];
        return window;
    }

    /// <summary>
    /// The held window after <paramref name="batch"/>, or null when it can't be worked out without the
    /// database: fewer than <c>limit</c> documents left while more exist, or a document the filter or
    /// comparer can't evaluate.
    /// </summary>
    public HeldWindow? TryApply(HeldWindow held, ChangeBatch batch)
    {
        try
        {
            var documents = new List<Document>(held.Documents);
            var complete = held.Complete;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in batch.Records)
            {
                if (!QueryListenerRegistry.InScope(_query, record.Collection) || !seen.Add(record.Path)) continue;

                var at = documents.FindIndex(d => d.Path == record.Path);
                if (at >= 0) documents.RemoveAt(at);

                // The batch holds each changed document's current state; absent = it doesn't exist now.
                if (!batch.Documents.TryGetValue(record.Path, out var doc) || !Matches(doc)) continue;

                // Held is every match (complete) or a prefix of them: a match past its end may sit behind
                // documents we don't hold, so it isn't placed.
                if (complete || (documents.Count > 0 && Compare(doc, documents[^1]) < 0))
                    Insert(documents, doc);
            }

            if (documents.Count > _held)
            {
                documents.RemoveRange(_held, documents.Count - _held);
                complete = false;
            }
            return !complete && documents.Count < _limit ? null : new HeldWindow(documents, complete);
        }
        catch (Exception)
        {
            return null;   // anything unexpected (a regex timeout, an uncomparable value): let the database decide
        }
    }

    private bool Matches(Document doc) =>
        (_predicate is null || FilterEvaluator.Matches(_predicate, doc.Path, doc.Fields)) && InRange(doc);

    // Cursors bound the sort order: the document's sort-key prefix against the cursor values, as CursorSql.
    private bool InRange(Document doc)
    {
        if (_range is null) return true;
        if (_range.Lower is { } lower)
        {
            var c = CompareToBound(doc, lower.Values);
            if (c < 0 || (c == 0 && !lower.Inclusive)) return false;
        }
        if (_range.Upper is { } upper)
        {
            var c = CompareToBound(doc, upper.Values);
            if (c > 0 || (c == 0 && !upper.Inclusive)) return false;
        }
        return true;
    }

    private int CompareToBound(Document doc, IReadOnlyList<Value> bound)
    {
        for (var i = 0; i < bound.Count && i < _keys.Count; i++)
        {
            var key = _keys[i];
            // __name__ cursor values are paths, given as a string or a reference.
            var b = key.Field.Equals(Name) && bound[i] is StringValue s ? new ReferenceValue(s.Value) : bound[i];
            var c = ValueComparer.Instance.Compare(KeyOf(key, doc), b);
            if (c != 0) return key.Direction == SortDirection.Desc ? -c : c;
        }
        return 0;
    }

    private void Insert(List<Document> documents, Document doc)
    {
        int lo = 0, hi = documents.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (Compare(documents[mid], doc) < 0) lo = mid + 1; else hi = mid;
        }
        documents.Insert(lo, doc);
    }

    private int Compare(Document a, Document b)
    {
        foreach (var key in _keys)
        {
            var c = ValueComparer.Instance.Compare(KeyOf(key, a), KeyOf(key, b));
            if (c != 0) return key.Direction == SortDirection.Desc ? -c : c;
        }
        return 0;
    }

    private static Value KeyOf(SortKey key, Document doc) =>
        key.Field.Equals(Name)
            ? new ReferenceValue(doc.Path)
            // orderBy fields carry an implicit exists filter, so every matching document has them.
            : FilterEvaluator.ResolveField(key.Field, doc.Fields)
              ?? throw new InvalidOperationException($"'{doc.Path}' has no '{key.Field}' to sort by");
}

/// <summary>
/// <c>select</c> applied to a document's fields, as ProjectionSql does it in SQL: absent paths and paths
/// through a non-map are omitted; when a field and a path below it are both selected, the field wins.
/// </summary>
internal static class Projection
{
    private sealed class Node
    {
        public bool Leaf;
        public Dictionary<string, Node> Children { get; } = new(StringComparer.Ordinal);
    }

    public static IReadOnlyDictionary<string, Value> Apply(IReadOnlyList<FieldPath> select, IReadOnlyDictionary<string, Value> fields)
    {
        var root = new Node();
        foreach (var path in select)
        {
            var node = root;
            foreach (var segment in path.Segments)
            {
                if (node.Leaf) break;
                if (!node.Children.TryGetValue(segment, out var child)) node.Children[segment] = child = new Node();
                node = child;
            }
            node.Leaf = true;
            node.Children.Clear();
        }
        return Pick(root, fields);
    }

    private static Dictionary<string, Value> Pick(Node node, IReadOnlyDictionary<string, Value> fields)
    {
        var picked = new Dictionary<string, Value>(StringComparer.Ordinal);
        foreach (var (name, child) in node.Children)
        {
            if (!fields.TryGetValue(name, out var value)) continue;
            if (child.Leaf) picked[name] = value;
            else if (value is MapValue map) picked[name] = new MapValue(Pick(child, map.Fields));
        }
        return picked;
    }
}
