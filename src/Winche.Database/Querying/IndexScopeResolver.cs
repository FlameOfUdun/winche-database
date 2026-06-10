using System.Text.RegularExpressions;
using Winche.Database.Documents;

namespace Winche.Database.Querying;

/// <summary>
/// Resolves, for a concrete collection, the SQL regex predicate bodies of every registered wildcard
/// index pattern that matches it. The C# regex guard guarantees each emitted predicate is satisfiable
/// for the collection, so a wrong predicate can never silently zero a query result.
/// </summary>
public sealed class IndexScopeResolver
{
    private readonly (string Pattern, string Regex)[] _patterns;
    private readonly IPathPatternMatcher _matcher;

    public IndexScopeResolver(IEnumerable<IndexDefinition> indexes, IPathPatternMatcher matcher)
    {
        _matcher = matcher;
        _patterns = indexes.Select(i => i.Path)
            .Where(DocumentPathParser.IsCollectionPattern)
            .Distinct()
            .Select(p => (p, DocumentPathParser.CollectionPatternRegex(p)))
            .ToArray();
    }

    public IReadOnlyList<string> ScopeRegexes(string collection) =>
        _patterns
            .Where(p => _matcher.Match(p.Pattern, collection).IsMatch
                        && Regex.IsMatch(collection, p.Regex))
            .Select(p => p.Regex)
            .ToList();
}
