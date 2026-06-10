using Winche.Database.Querying.Ast;
using Winche.Rules.Expressions;
using Winche.Rules.Querying;

namespace Winche.Database.Authorization;

/// <summary>
/// Converts a Winche <see cref="Query"/> to a <see cref="QueryConstraints"/> for the rules engine.
/// </summary>
/// <remarks>
/// <para>
/// Only <see cref="FilterOperator"/> values that map 1:1 onto <see cref="CompareOp"/> are converted.
/// The following operators are skipped (omitting a constraint is always safe — the engine is conservative):
/// <list type="bullet">
///   <item><see cref="FilterOperator.In"/> / <see cref="FilterOperator.NotIn"/></item>
///   <item><see cref="FilterOperator.ArrayContains"/> / <see cref="FilterOperator.ArrayContainsAny"/> / <see cref="FilterOperator.ArrayContainsAll"/></item>
///   <item><see cref="FilterOperator.Contains"/> / <see cref="FilterOperator.StartsWith"/> / <see cref="FilterOperator.EndsWith"/> / <see cref="FilterOperator.Regex"/></item>
/// </list>
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
                if (TryMapOperator(ff.Op, out var op))
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
    /// Maps <paramref name="op"/> to <see cref="CompareOp"/>.
    /// Returns <c>false</c> for operators with no sound 1:1 mapping.
    /// </summary>
    private static bool TryMapOperator(FilterOperator op, out CompareOp result)
    {
        switch (op)
        {
            case FilterOperator.Eq:  result = CompareOp.Eq; return true;
            case FilterOperator.Ne:  result = CompareOp.Ne; return true;
            case FilterOperator.Lt:  result = CompareOp.Lt; return true;
            case FilterOperator.Lte: result = CompareOp.Le; return true;
            case FilterOperator.Gt:  result = CompareOp.Gt; return true;
            case FilterOperator.Gte: result = CompareOp.Ge; return true;
            default:
                result = default;
                return false;
        }
    }
}
