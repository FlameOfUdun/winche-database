using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Values;

namespace Winche.Database.Querying.Planning;

internal abstract record PlanNode;

internal sealed record CollectionScan(string Collection) : PlanNode;

/// <summary>WHERE predicate. In Phase 3 also used after Group as HAVING.</summary>
internal sealed record FilterNode(Filter Predicate) : PlanNode;

internal sealed record SortKey(FieldPath Field, SortDirection Direction);

internal sealed record SortNode(IReadOnlyList<SortKey> Keys) : PlanNode;

internal sealed record SortBoundary(IReadOnlyList<Value> Values, bool Inclusive);

/// <summary>Cursor bounds, tied to the SortNode that precedes this node in the plan.</summary>
internal sealed record CursorRangeNode(SortBoundary? Lower, SortBoundary? Upper) : PlanNode;

internal sealed record PageNode(int Limit, int Skip, bool FetchExtraRow) : PlanNode;

internal sealed record LogicalPlan(IReadOnlyList<PlanNode> Nodes);

internal sealed record LookupNode(
    string Collection,
    FieldPath LocalField,
    FieldPath ForeignField,
    string As,
    Filter? Filter,
    IReadOnlyList<SortKey>? OrderBy,
    int Limit) : PlanNode;

internal sealed record UnwindNode(FieldPath Field, string As, bool PreserveNullAndEmpty) : PlanNode;

internal sealed record GroupKey(string As, FieldPath Field);
internal sealed record Accumulator(string As, AggFunction Fn, FieldPath? Field);

internal sealed record GroupNode(
    IReadOnlyList<GroupKey> Keys,
    IReadOnlyList<Accumulator> Accumulators) : PlanNode;

internal abstract record ProjectExpr;
internal sealed record FieldRefExpr(FieldPath Field) : ProjectExpr;
internal sealed record LiteralExpr(Value Value) : ProjectExpr;
internal sealed record AggFuncExpr(AggFunction Fn, FieldPath? Field) : ProjectExpr;

internal sealed record Projection(string As, ProjectExpr Expr);
internal sealed record ProjectNode(IReadOnlyList<Projection> Projections) : PlanNode;

internal sealed record LimitNode(int Count) : PlanNode;
internal sealed record SkipNode(int Count) : PlanNode;
