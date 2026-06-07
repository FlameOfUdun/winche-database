using Winche.Database.Documents;
using Winche.Sentinel.Interfaces;
using Winche.Sentinel.Models;

namespace Winche.Database.IntegrationTests;

/// <summary>A path pattern matcher that always returns no-match (used in tests to suppress hook dispatch).</summary>
internal sealed class NoOpMatcher : IPathPatternMatcher<Document>
{
    public PathMatchResult Match(string pattern, string path) => PathMatchResult.NoMatch;
}
