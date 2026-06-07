using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Values;

namespace Winche.Database.Querying.Planning;

public abstract record PlanNode;

public sealed record CollectionScan(string Collection) : PlanNode;

/// <summary>WHERE predicate. In Phase 3 also used after Group as HAVING.</summary>
public sealed record FilterNode(FilterAst Predicate) : PlanNode;

public sealed record SortKey(FieldPath Field, SortDirection Direction);

public sealed record SortNode(IReadOnlyList<SortKey> Keys) : PlanNode;

public sealed record SortBoundary(IReadOnlyList<Value> Values, bool Inclusive);

/// <summary>Cursor bounds, tied to the SortNode that precedes this node in the plan.</summary>
public sealed record CursorRangeNode(SortBoundary? Lower, SortBoundary? Upper) : PlanNode;

public sealed record PageNode(int Limit, int Skip, bool FetchExtraRow) : PlanNode;

public sealed record LogicalPlan(IReadOnlyList<PlanNode> Nodes);

public sealed record LookupNode(
    string Collection,
    FieldPath LocalField,
    FieldPath ForeignField,
    string As,
    FilterAst? Filter,
    IReadOnlyList<SortKey>? OrderBy,
    int Limit) : PlanNode;

public sealed record UnwindNode(FieldPath Field, string As, bool PreserveNullAndEmpty) : PlanNode;

public sealed record GroupKey(string As, FieldPath Field);
public sealed record Accumulator(string As, AggFunction Fn, FieldPath? Field);

public sealed record GroupNode(
    IReadOnlyList<GroupKey> Keys,
    IReadOnlyList<Accumulator> Accumulators) : PlanNode;

public abstract record ProjectExpr;
public sealed record FieldRefExpr(FieldPath Field) : ProjectExpr;
public sealed record LiteralExpr(Value Value) : ProjectExpr;
public sealed record AggFuncExpr(AggFunction Fn, FieldPath? Field) : ProjectExpr;

public sealed record Projection(string As, ProjectExpr Expr);
public sealed record ProjectNode(IReadOnlyList<Projection> Projections) : PlanNode;

public sealed record LimitNode(int Count) : PlanNode;
public sealed record SkipNode(int Count) : PlanNode;
