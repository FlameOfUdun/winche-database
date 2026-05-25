namespace Winche.Database.AST.Models;

public abstract record PipelineStage;

public sealed record MatchStage(
    string Collection,
    WhereNode? Filter
) : PipelineStage;

public sealed record LookupStage(
    string Collection,
    string LocalField,
    string ForeignField,
    string As,
    WhereNode? Filter = null,
    List<SortNode>? OrderBy = null,
    int Limit = 100
) : PipelineStage;

public sealed record UnwindStage(
    string Field,
    string As,
    bool PreserveNullAndEmpty = false
) : PipelineStage;
public sealed record GroupStage(
    List<GroupKey> Keys,
    List<AccumulatorField> Accumulators,
    WhereNode? Having = null
) : PipelineStage;

public sealed record GroupKey(
    string As,
    string Field,
    FieldType? Type = null
);

public sealed record AccumulatorField(
    string As,
    AggFunction Function,
    string? Field = null,
    FieldType? Type = null
);

public sealed record ProjectStage(
    List<ProjectField> Fields
) : PipelineStage;


public sealed record ProjectField(
    string As,
    ProjectExpr Expression
);


public abstract record ProjectExpr;

public sealed record FieldRefExpr(
    string Field,
    FieldType? Type = null
) : ProjectExpr;

public sealed record LiteralExpr(
    object? Value
) : ProjectExpr;

public sealed record AggFuncExpr(
    AggFunction Function,
    string? Field = null,
    FieldType? Type = null
) : ProjectExpr;

public sealed record SortStage(
    List<SortNode> Fields
) : PipelineStage;

public sealed record LimitStage(int Count) : PipelineStage;

public sealed record SkipStage(int Count) : PipelineStage;