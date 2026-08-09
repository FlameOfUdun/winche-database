using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Planning;

namespace Winche.Database.Querying;

/// <summary>
/// Validates a query's shape against a <see cref="QueryLimits"/> ceiling — the same checks the
/// planner applies internally, exposed so a transport can reject an oversized request with its
/// own error mapping before the query ever reaches <see cref="QueryExecutor"/>. All violations are
/// <see cref="PlanValidationException"/> (INVALID_QUERY).
/// </summary>
public static class QueryShapeValidator
{
    public static void Validate(Query query, QueryLimits limits) => Normalizer.Normalize(query, limits);
}
