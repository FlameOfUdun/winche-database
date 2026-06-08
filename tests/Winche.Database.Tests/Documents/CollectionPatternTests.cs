using Winche.Database.Documents;

namespace Winche.Database.Tests.Documents;

public class CollectionPatternTests
{
    [Theory]
    [InlineData("userData/*/sessionHistory", true)]
    [InlineData("users", false)]
    [InlineData("userData/alice/sessionHistory", false)]
    public void IsCollectionPattern_DetectsStar(string path, bool expected) =>
        Assert.Equal(expected, DocumentPathParser.IsCollectionPattern(path));

    [Theory]
    [InlineData("userData/*/sessionHistory", "^userData/[^/]+/sessionHistory$")]
    [InlineData("orgs/acme/teams/*/members", "^orgs/acme/teams/[^/]+/members$")]
    [InlineData("orgs/*/teams/*/members", "^orgs/[^/]+/teams/[^/]+/members$")]
    public void CollectionPatternRegex_BuildsAnchoredRegex(string path, string expected) =>
        Assert.Equal(expected, DocumentPathParser.CollectionPatternRegex(path));

    [Theory]
    [InlineData("users")]
    [InlineData("userData/*/sessionHistory")]
    [InlineData("orgs/acme/teams/*/members")]
    [InlineData("orgs/*/teams/*/members")]
    public void IsValidIndexPath_AcceptsValid(string path) =>
        Assert.True(DocumentPathParser.IsValidIndexPath(path, out _));

    [Theory]
    [InlineData("")]                            // empty
    [InlineData("userData/alice")]              // even segment count
    [InlineData("userData//sessionHistory")]    // empty segment
    [InlineData("*/sessionHistory")]            // '*' at name position
    [InlineData("userData/**/sessionHistory")]  // not a bare '*'
    [InlineData("userData/a*/sessionHistory")]  // partial-segment glob
    [InlineData("user.Data/*/sessionHistory")]  // bad charset in literal
    public void IsValidIndexPath_RejectsInvalid(string path)
    {
        Assert.False(DocumentPathParser.IsValidIndexPath(path, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }
}
