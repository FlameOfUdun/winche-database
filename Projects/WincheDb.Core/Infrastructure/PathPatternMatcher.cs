using System.Collections.Immutable;

namespace WincheDb.Core.Infrastructure;

public sealed class PathMatchResult
{
    public static PathMatchResult NoMatch { get; } = new(false, ImmutableDictionary<string, string>.Empty);

    public bool IsMatch { get; }
    public IReadOnlyDictionary<string, string> Params { get; }

    private PathMatchResult(bool isMatch, IReadOnlyDictionary<string, string> params_)
    {
        IsMatch = isMatch;
        Params = params_;
    }

    internal static PathMatchResult Match(IReadOnlyDictionary<string, string> params_) => new(true, params_);
}

public static class PathPatternMatcher
{
    public static PathMatchResult Match(string pattern, string path)
    {
        var patternSegments = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var pathSegments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (patternSegments.Length == 0 || pathSegments.Length == 0)
            return PathMatchResult.NoMatch;

        var captured = new Dictionary<string, string>();
        var pi = 0;

        for (var i = 0; i < patternSegments.Length; i++)
        {
            var seg = patternSegments[i];

            if (seg == "**")
            {
                if (i != patternSegments.Length - 1)
                    throw new ArgumentException("'**' must be the last segment in the pattern.");

                return PathMatchResult.Match(captured);
            }

            if (pi >= pathSegments.Length)
                return PathMatchResult.NoMatch;

            if (seg.Length > 2 && seg[0] == '{' && seg[^1] == '}')
            {
                captured[seg[1..^1]] = pathSegments[pi];
            }
            else if (seg != "*" && !string.Equals(seg, pathSegments[pi], StringComparison.Ordinal))
            {
                return PathMatchResult.NoMatch;
            }

            pi++;
        }

        if (pi != pathSegments.Length)
            return PathMatchResult.NoMatch;

        return PathMatchResult.Match(captured);
    }
}
