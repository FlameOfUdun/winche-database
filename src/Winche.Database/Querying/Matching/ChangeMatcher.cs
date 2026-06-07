using Winche.Database.Models;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Planning;
using Winche.Database.Values;

namespace Winche.Database.Querying.Matching;

/// <summary>
/// Conservative "could this change affect this query's results?" — replaces the old QueryMatcher.
/// Evaluates the Normalizer-prepared predicate (incl. implicit orderBy-Exists); limit/cursors
/// are ignored (a positive answer triggers a full requery which is the source of truth).
/// </summary>
public static class ChangeMatcher
{
    /// <summary>
    /// Tries to extract the normalized predicate from a query. Returns false if the query
    /// cannot be normalized (e.g. PlanValidationException); on false, predicate is null and
    /// the caller must treat the query as always-affected (conservative).
    /// </summary>
    public static bool TryPreparePredicate(QueryAst query, out FilterAst? predicate)
    {
        try
        {
            predicate = Normalizer.Normalize(query).Nodes.OfType<FilterNode>().FirstOrDefault()?.Predicate;
            return true;
        }
        catch (PlanValidationException)
        {
            predicate = null;
            return false;
        }
    }

    public static bool CouldAffect(
        QueryAst query,
        IReadOnlySet<string> snapshotIds,
        string changedId,
        bool isRemoved,
        string path,
        IReadOnlyDictionary<string, Value>? fields)
    {
        var inSnapshot = snapshotIds.Contains(changedId);
        if (isRemoved) return inSnapshot;
        if (fields is null) return true;                       // no data → be conservative

        bool matches;
        try
        {
            var predicate = Normalizer.Normalize(query).Nodes.OfType<FilterNode>().FirstOrDefault()?.Predicate;
            matches = predicate is null || FilterEvaluator.Matches(predicate, path, fields);
        }
        catch (Exception)
        {
            return true;    // any evaluation failure (bad regex, timeout, …) → conservative requery
        }
        return inSnapshot || matches;
    }

    /// <summary>
    /// Overload that uses the cached predicate from <see cref="QueryGroup"/> to avoid
    /// re-normalizing the query on every change event.
    /// </summary>
    public static bool CouldAffect(
        QueryGroup group,
        IReadOnlySet<string> snapshotIds,
        string changedId,
        bool isRemoved,
        string path,
        IReadOnlyDictionary<string, Value>? fields)
    {
        var inSnapshot = snapshotIds.Contains(changedId);
        if (isRemoved) return inSnapshot;
        if (fields is null) return true;                       // no data → be conservative

        // If predicate preparation failed, stay conservative
        if (group.PredicateFailed) return true;

        bool matches;
        try
        {
            matches = group.Predicate is null || FilterEvaluator.Matches(group.Predicate, path, fields);
        }
        catch (Exception)
        {
            return true;    // any evaluation failure (bad regex, timeout, …) → conservative requery
        }
        return inSnapshot || matches;
    }
}
