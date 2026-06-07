using System.Text.Json.Serialization;
using Winche.Database.Documents;
using Winche.Database.Values;

namespace Winche.Database.Querying.Ast;

public sealed record OrderAst(FieldPath Field, SortDirection Direction = SortDirection.Asc);

public sealed record CursorAst(IReadOnlyList<Value> Values, bool Before)
{
    public bool Equals(CursorAst? other) =>
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
public sealed record QueryAst(
    string Collection,
    FilterAst? Where = null,
    IReadOnlyList<OrderAst>? OrderBy = null,
    int? Limit = null,
    CursorAst? Start = null,
    CursorAst? End = null)
{
    public bool Equals(QueryAst? other) =>
        other is not null
        && Collection == other.Collection
        && Equals(Where, other.Where)
        && (OrderBy is null ? other.OrderBy is null : other.OrderBy is not null && OrderBy.SequenceEqual(other.OrderBy))
        && Limit == other.Limit
        && Equals(Start, other.Start)
        && Equals(End, other.End);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Collection);
        hash.Add(Where);
        if (OrderBy is not null) foreach (var o in OrderBy) hash.Add(o);
        hash.Add(Limit);
        hash.Add(Start);
        hash.Add(End);
        return hash.ToHashCode();
    }
}
