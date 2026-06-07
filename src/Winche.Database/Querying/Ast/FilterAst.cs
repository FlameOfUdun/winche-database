using Winche.Database.Documents;
using Winche.Database.Values;

namespace Winche.Database.Querying.Ast;

public abstract record FilterAst;

public sealed record FieldFilterAst(FieldPath Field, FilterOperator Op, Value Operand) : FilterAst;

public sealed record CompositeFilterAst(CompositeOp Op, IReadOnlyList<FilterAst> Filters) : FilterAst
{
    public bool Equals(CompositeFilterAst? other) =>
        other is not null && Op == other.Op && Filters.SequenceEqual(other.Filters);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Op);
        foreach (var f in Filters) hash.Add(f);
        return hash.ToHashCode();
    }
}

public sealed record UnaryFilterAst(FieldPath Field, UnaryOp Op) : FilterAst;

public sealed record FieldCompareAst(FieldPath Left, FilterOperator Op, FieldPath Right) : FilterAst;
