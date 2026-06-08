using Microsoft.Extensions.DependencyInjection;
using Winche.Database.Documents;
using Winche.Database.Querying;
using Winche.Database.Querying.Ast;
using Winche.Sentinel.DependencyInjection;
using Winche.Sentinel.Interfaces;

namespace Winche.Database.Tests.Querying;

file sealed class UserSessionsIndex : IndexDefinition
{
    public override string Path => "userData/*/sessionHistory";
    public override IReadOnlyList<IndexField> Fields => [new("startedAt")];
}

public class IndexScopeResolverTests
{
    private static IPathPatternMatcher<Document> Matcher() =>
        new ServiceCollection().AddWincheSentinel<Document>()
            .BuildServiceProvider().GetRequiredService<IPathPatternMatcher<Document>>();

    [Fact]
    public void ScopeRegexes_MatchingCollection_ReturnsPatternRegex()
    {
        var resolver = new IndexScopeResolver([new UserSessionsIndex()], Matcher());
        Assert.Equal(
            new[] { "^userData/[^/]+/sessionHistory$" },
            resolver.ScopeRegexes("userData/alice/sessionHistory"));
    }

    [Fact]
    public void ScopeRegexes_NonMatchingCollection_ReturnsEmpty()
    {
        var resolver = new IndexScopeResolver([new UserSessionsIndex()], Matcher());
        Assert.Empty(resolver.ScopeRegexes("adminData/root/sessionHistory"));
        Assert.Empty(resolver.ScopeRegexes("users")); // top-level
    }
}
