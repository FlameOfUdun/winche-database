using Winche.Database.Documents;
using Winche.Database.Values;

namespace Winche.Database.Querying.Ast;

public abstract record Filter;

public sealed record FieldFilter(FieldPath Field, FilterOperator Op, Value Operand) : Filter;

public sealed record CompositeFilter(CompositeOp Op, IReadOnlyList<Filter> Filters) : Filter
{
    public bool Equals(CompositeFilter? other) =>
        other is not null && Op == other.Op && Filters.SequenceEqual(other.Filters);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Op);
        foreach (var f in Filters) hash.Add(f);
        return hash.ToHashCode();
    }
}

public sealed record UnaryFilter(FieldPath Field, UnaryOp Op) : Filter;

public sealed record FieldCompare(FieldPath Left, FilterOperator Op, FieldPath Right) : Filter;
