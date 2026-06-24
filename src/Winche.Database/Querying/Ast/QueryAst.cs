using System.Text.Json.Serialization;
using Winche.Database.Documents;
using Winche.Database.Values;

namespace Winche.Database.Querying.Ast;

public sealed record Ordering(FieldPath Field, SortDirection Direction = SortDirection.Asc);

public sealed record Cursor(IReadOnlyList<Value> Values, bool Before)
{
    public bool Equals(Cursor? other) =>
        other is not null && Before == other.Before && Values.SequenceEqual(other.Values);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Before);
        foreach (var v in Values) hash.Add(v);
        return hash.ToHashCode();
    }

    private static readonly FieldPath NamePath = FieldPath.Parse("__name__");

    /// <summary>
    /// Builds a cursor from a document snapshot, deriving one value per sort key in order:
    /// each orderBy field's value from the document, then the implicit __name__ tiebreaker as
    /// ReferenceValue(doc.Path). Matches the reference startAt/After(doc) resolution behaviour.
    /// A null/empty orderBy yields a __name__-only cursor. Throws ArgumentException if the
    /// document is missing an orderBy field.
    /// </summary>
    public static Cursor FromDocument(Document doc, IReadOnlyList<Ordering>? orderBy, bool before)
    {
        // Mirrors Normalizer.BuildSortKeys: append the __name__ tiebreaker unless it is already explicit.
        var ordering = orderBy ?? [];
        var sawName = ordering.Any(o => o.Field.Equals(NamePath));

        var values = new List<Value>(ordering.Count + (sawName ? 0 : 1));
        foreach (var o in ordering)
            values.Add(o.Field.Equals(NamePath) ? new ReferenceValue(doc.Path) : ExtractValue(doc, o.Field));
        if (!sawName)
            values.Add(new ReferenceValue(doc.Path));

        return new Cursor(values, before);
    }

    private static Value ExtractValue(Document doc, FieldPath field)
    {
        IReadOnlyDictionary<string, Value> current = doc.Fields;
        for (var i = 0; i < field.Segments.Count; i++)
        {
            var segment = field.Segments[i];
            if (!current.TryGetValue(segment, out var value))
                throw new ArgumentException($"Document '{doc.Path}' is missing cursor field '{field}'.");
            if (i == field.Segments.Count - 1)
                return value;
            if (value is not MapValue map)
                throw new ArgumentException($"Document '{doc.Path}' cursor field '{field}' descends through a non-map at '{segment}'.");
            current = map.Fields;
        }
        throw new InvalidOperationException("Unreachable: FieldPath.Parse rejects empty paths.");
    }
}

[JsonConverter(typeof(Serialization.QueryAstJsonConverter))]
public sealed record Query(
    string Collection,
    Filter? Where = null,
    IReadOnlyList<Ordering>? OrderBy = null,
    int? Limit = null,
    Cursor? Start = null,
    Cursor? End = null,
    IReadOnlyList<FieldPath>? Select = null,
    int? Offset = null,
    int? LimitToLast = null)
{
    public bool Equals(Query? other) =>
        other is not null
        && Collection == other.Collection
        && Equals(Where, other.Where)
        && (OrderBy is null ? other.OrderBy is null : other.OrderBy is not null && OrderBy.SequenceEqual(other.OrderBy))
        && Limit == other.Limit
        && Equals(Start, other.Start)
        && Equals(End, other.End)
        && (Select is null ? other.Select is null : other.Select is not null && Select.SequenceEqual(other.Select))
        && Offset == other.Offset
        && LimitToLast == other.LimitToLast;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Collection);
        hash.Add(Where);
        if (OrderBy is not null) foreach (var o in OrderBy) hash.Add(o);
        hash.Add(Limit);
        hash.Add(Start);
        hash.Add(End);
        if (Select is not null) foreach (var s in Select) hash.Add(s);
        hash.Add(Offset);
        hash.Add(LimitToLast);
        return hash.ToHashCode();
    }
}
