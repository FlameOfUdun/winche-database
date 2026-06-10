using Winche.Database.Documents;
using Winche.Database.Querying;

namespace Winche.Database.Tests.Querying;

public class IndexScopeResolverTests
{
    private static IPathPatternMatcher Matcher() => PathPatternMatcher.Instance;

    private static readonly IndexDefinition UserSessionsIndex =
        new("userData/*/sessionHistory", [new("startedAt")]);

    [Fact]
    public void ScopeRegexes_MatchingCollection_ReturnsPatternRegex()
    {
        var resolver = new IndexScopeResolver([UserSessionsIndex], Matcher());
        Assert.Equal(
            new[] { "^userData/[^/]+/sessionHistory$" },
            resolver.ScopeRegexes("userData/alice/sessionHistory"));
    }

    [Fact]
    public void ScopeRegexes_NonMatchingCollection_ReturnsEmpty()
    {
        var resolver = new IndexScopeResolver([UserSessionsIndex], Matcher());
        Assert.Empty(resolver.ScopeRegexes("adminData/root/sessionHistory"));
        Assert.Empty(resolver.ScopeRegexes("users")); // top-level
    }
}
