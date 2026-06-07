// src/Winche.Database/Querying/Ast/PipelineAst.cs
using System.Text.Json.Serialization;
using Winche.Database.Documents;
using Winche.Database.Values;

namespace Winche.Database.Querying.Ast;

public enum AggFunction { Count, Sum, Avg, Min, Max, Push, AddToSet, First, Last }

public abstract record StageAst;

public sealed record MatchStageAst(string Collection, FilterAst? Where) : StageAst;

public sealed record FilterStageAst(FilterAst Where) : StageAst;

public sealed record LookupStageAst(
    string Collection,
    FieldPath LocalField,
    FieldPath ForeignField,
    string As,
    FilterAst? Where = null,
    IReadOnlyList<OrderAst>? OrderBy = null,
    int Limit = 100) : StageAst;

public sealed record UnwindStageAst(FieldPath Field, string As, bool PreserveNullAndEmpty = false) : StageAst;

public sealed record GroupKeyAst(string As, FieldPath Field);
public sealed record AccumulatorAst(string As, AggFunction Fn, FieldPath? Field = null);

public sealed record GroupStageAst(
    IReadOnlyList<GroupKeyAst> Keys,
    IReadOnlyList<AccumulatorAst> Accumulators,
    FilterAst? Having = null) : StageAst
{
    public bool Equals(GroupStageAst? other) =>
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

public abstract record ProjectExprAst;
public sealed record FieldRefExprAst(FieldPath Field) : ProjectExprAst;
public sealed record LiteralExprAst(Value Value) : ProjectExprAst;
public sealed record AggFuncExprAst(AggFunction Fn, FieldPath? Field = null) : ProjectExprAst;

public sealed record ProjectionAst(string As, ProjectExprAst Expr);
public sealed record ProjectStageAst(IReadOnlyList<ProjectionAst> Fields) : StageAst;

public sealed record SortStageAst(IReadOnlyList<OrderAst> Fields) : StageAst;
public sealed record LimitStageAst(int Count) : StageAst;
public sealed record SkipStageAst(int Count) : StageAst;

[JsonConverter(typeof(Serialization.PipelineAstJsonConverter))]
public sealed record PipelineAst(IReadOnlyList<StageAst> Stages);
