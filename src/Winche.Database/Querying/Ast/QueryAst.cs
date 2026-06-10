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
}

[JsonConverter(typeof(Serialization.QueryAstJsonConverter))]
public sealed record Query(
    string Collection,
    Filter? Where = null,
    IReadOnlyList<Ordering>? OrderBy = null,
    int? Limit = null,
    Cursor? Start = null,
    Cursor? End = null,
    IReadOnlyList<FieldPath>? Select = null)
{
    public bool Equals(Query? other) =>
        other is not null
        && Collection == other.Collection
        && Equals(Where, other.Where)
        && (OrderBy is null ? other.OrderBy is null : other.OrderBy is not null && OrderBy.SequenceEqual(other.OrderBy))
        && Limit == other.Limit
        && Equals(Start, other.Start)
        && Equals(End, other.End)
        && (Select is null ? other.Select is null : other.Select is not null && Select.SequenceEqual(other.Select));

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
        return hash.ToHashCode();
    }
}
