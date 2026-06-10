using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Values;

namespace Winche.Database.Querying.Planning;

internal abstract record PlanNode;

internal sealed record CollectionScan(string Collection) : PlanNode;

/// <summary>WHERE predicate.</summary>
internal sealed record FilterNode(Filter Predicate) : PlanNode;

internal sealed record SortKey(FieldPath Field, SortDirection Direction);

internal sealed record SortNode(IReadOnlyList<SortKey> Keys) : PlanNode;

internal sealed record SortBoundary(IReadOnlyList<Value> Values, bool Inclusive);

/// <summary>Cursor bounds, tied to the SortNode that precedes this node in the plan.</summary>
internal sealed record CursorRangeNode(SortBoundary? Lower, SortBoundary? Upper) : PlanNode;

internal sealed record PageNode(int Limit, int Skip, bool FetchExtraRow) : PlanNode;

internal sealed record LogicalPlan(IReadOnlyList<PlanNode> Nodes);
