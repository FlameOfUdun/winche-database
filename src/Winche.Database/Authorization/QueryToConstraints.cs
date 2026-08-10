using Winche.Database.Querying.Ast;
using Winche.Rules.Expressions;
using Winche.Rules.Querying;

namespace Winche.Database.Authorization;

/// <summary>
/// Converts a Winche <see cref="Query"/> to a <see cref="QueryConstraints"/> for the rules engine.
/// </summary>
/// <remarks>
/// <para>
/// Only <see cref="FilterOperator"/> values that map 1:1 onto <see cref="ComparisonOperator"/> are converted.
/// The following operators are skipped (omitting a constraint is always safe — the engine is conservative):
/// <list type="bullet">
///   <item><see cref="FilterOperator.In"/> / <see cref="FilterOperator.NotIn"/></item>
///   <item><see cref="FilterOperator.ArrayContains"/> / <see cref="FilterOperator.ArrayContainsAny"/> / <see cref="FilterOperator.ArrayContainsAll"/></item>
///   <item><see cref="FilterOperator.Contains"/> / <see cref="FilterOperator.StartsWith"/> / <see cref="FilterOperator.EndsWith"/> / <see cref="FilterOperator.Regex"/></item>
/// </list>
/// </para>
/// <para>
/// A filter whose first field-path segment is one of <see cref="ReservedResourceFields.Names"/> is
/// also skipped. <see cref="DocumentToResource"/> binds those names to storage columns in the
/// <c>resource</c> map, but query execution resolves them against the user-writable data column
/// instead — proving a rule safe against the column and then filtering on the data would let a
/// document with a forged data field of that name defeat the rule. Matching on the first segment
/// only means <c>id.sub</c> is skipped too: a rule on <c>resource.id.sub</c> would traverse the
/// column-backed string while SQL traverses nested JSONB, the same disagreement one level down.
/// </para>
/// <para>
/// Composite <see cref="CompositeOp.Or"/> or <see cref="CompositeOp.Not"/> top-level filters are also
/// skipped entirely (no safe way to extract per-result guarantees from a disjunction).
/// Only top-level AND-ed <see cref="FieldFilter"/>s, or a bare <see cref="FieldFilter"/>, are converted.
/// </para>
/// </remarks>
internal static class QueryToConstraints
{
    /// <summary>Converts <paramref name="query"/> to a <see cref="QueryConstraints"/> for the rules engine.</summary>
    public static QueryConstraints Convert(Query query)
    {
        var constraints = new List<QueryConstraint>();
        CollectConstraints(query.Where, constraints);
        return new QueryConstraints(query.Collection, constraints);
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private static void CollectConstraints(Filter? filter, List<QueryConstraint> output)
    {
        switch (filter)
        {
            case null:
                break;

            case FieldFilter ff:
                // A filter on a reserved name (see ReservedResourceFields) is skipped: the rule's
                // resource.<name> resolves to a storage column, but SQL filters on the data column
                // for that same name — proving safety off the constraint would be unsound.
                if (ReservedResourceFields.Names.Contains(ff.Field.Segments[0]))
                    break;
                if (TryMapOperator(ff.Op, out var op))
                    // Fields are exposed at the top of the resource map (see DocumentToResource),
                    // so a query on field X maps directly to the rule path resource.X.
                    output.Add(new QueryConstraint(ff.Field.Segments, op, ValueToRuleValue.Convert(ff.Operand)));
                break;

            case CompositeFilter { Op: CompositeOp.And } cf:
                // Recurse only into AND nodes — every child is a guaranteed constraint.
                foreach (var child in cf.Filters)
                    CollectConstraints(child, output);
                break;

            // OR / NOT at the top level (or nested after an AND) cannot be decomposed into
            // per-result guarantees — skip them.
            default:
                break;
        }
    }

    /// <summary>
    /// Maps <paramref name="op"/> to <see cref="ComparisonOperator"/>.
    /// Returns <c>false</c> for operators with no sound 1:1 mapping.
    /// </summary>
    private static bool TryMapOperator(FilterOperator op, out ComparisonOperator result)
    {
        switch (op)
        {
            case FilterOperator.Eq:  result = ComparisonOperator.Eq; return true;
            case FilterOperator.Ne:  result = ComparisonOperator.Ne; return true;
            case FilterOperator.Lt:  result = ComparisonOperator.Lt; return true;
            case FilterOperator.Lte: result = ComparisonOperator.Le; return true;
            case FilterOperator.Gt:  result = ComparisonOperator.Gt; return true;
            case FilterOperator.Gte: result = ComparisonOperator.Ge; return true;
            default:
                result = default;
                return false;
        }
    }
}
