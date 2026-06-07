using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Matching;

namespace Winche.Database.Models;

public sealed class QueryGroup
{
    public required string Key { get; init; }
    public required string Collection { get; init; }
    public required QueryAst Query { get; init; }
    public QuerySnapshot Snapshot = new();

    /// <summary>The normalized filter predicate, computed once at construction time.</summary>
    public FilterAst? Predicate { get; private set; }

    /// <summary>True if predicate preparation failed (unnormalizable query); callers must be conservative.</summary>
    public bool PredicateFailed { get; private set; }

    /// <summary>True when a predicate was successfully prepared (may be null if the query has no filter).</summary>
    public bool HasPredicate => !PredicateFailed;

    /// <summary>Initializes the cached predicate from the query. Call after setting <see cref="Query"/>.</summary>
    public void PreparePredicateFromQuery()
    {
        if (!ChangeMatcher.TryPreparePredicate(Query, out var predicate))
            PredicateFailed = true;
        else
            Predicate = predicate;
    }
}
