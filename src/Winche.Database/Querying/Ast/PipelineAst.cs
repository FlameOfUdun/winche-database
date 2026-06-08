// src/Winche.Database/Querying/Ast/Pipeline.cs
using System.Text.Json.Serialization;
using Winche.Database.Documents;
using Winche.Database.Values;

namespace Winche.Database.Querying.Ast;

public enum AggFunction { Count, Sum, Avg, Min, Max, Push, AddToSet, First, Last }

public abstract record Stage;

public sealed record Match(string Collection, Filter? Where) : Stage;

public sealed record Where(Filter Predicate) : Stage;

public sealed record Lookup(
    string Collection,
    FieldPath LocalField,
    FieldPath ForeignField,
    string As,
    Filter? Where = null,
    IReadOnlyList<Ordering>? OrderBy = null,
    int Limit = 100) : Stage;

public sealed record Unwind(FieldPath Field, string As, bool PreserveNullAndEmpty = false) : Stage;

public sealed record GroupKey(string As, FieldPath Field);
public sealed record Accumulator(string As, AggFunction Fn, FieldPath? Field = null);

public sealed record Group(
    IReadOnlyList<GroupKey> Keys,
    IReadOnlyList<Accumulator> Accumulators,
    Filter? Having = null) : Stage
{
    public bool Equals(Group? other) =>
        other is not null
        && Keys.SequenceEqual(other.Keys)
        && Accumulators.SequenceEqual(other.Accumulators)
        && Equals(Having, other.Having);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var k in Keys) hash.Add(k);
        foreach (var a in Accumulators) hash.Add(a);
        hash.Add(Having);
        return hash.ToHashCode();
    }
}

public abstract record ProjectExpr;
public sealed record FieldRefExpr(FieldPath Field) : ProjectExpr;
public sealed record LiteralExpr(Value Value) : ProjectExpr;
public sealed record AggFuncExpr(AggFunction Fn, FieldPath? Field = null) : ProjectExpr;

public sealed record Projection(string As, ProjectExpr Expr);
public sealed record Project(IReadOnlyList<Projection> Fields) : Stage;

public sealed record Sort(IReadOnlyList<Ordering> Fields) : Stage;
public sealed record Limit(int Count) : Stage;
public sealed record Skip(int Count) : Stage;

[JsonConverter(typeof(Serialization.PipelineAstJsonConverter))]
public sealed record Pipeline(IReadOnlyList<Stage> Stages);
