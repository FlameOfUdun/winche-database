namespace Winche.Database.Querying;

/// <summary>
/// Default <see cref="IPathPatternMatcher"/> implementation.
/// Supports literal segments, <c>*</c> (single-segment wildcard), <c>**</c> (multi-segment
/// trailing wildcard), and <c>{name}</c> named single-segment captures.
/// </summary>
internal sealed class PathPatternMatcher : IPathPatternMatcher
{
    public static readonly PathPatternMatcher Instance = new();

    public PathMatchResult Match(string pattern, string path)
    {
        var patternSegs = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var pathSegs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (patternSegs.Length == 0)
            return PathMatchResult.NoMatch;

        var captures = new Dictionary<string, string>(StringComparer.Ordinal);
        int pi = 0;
        for (int i = 0; i < patternSegs.Length; i++, pi++)
        {
            var seg = patternSegs[i];
            if (seg == "**")
            {
                if (i != patternSegs.Length - 1)
                    throw new ArgumentException("'**' must be the last segment in the pattern.", nameof(pattern));
                captures["**"] = string.Join('/', pathSegs[pi..]);
                return new PathMatchResult(true, captures);
            }

            if (pi >= pathSegs.Length)
                return PathMatchResult.NoMatch;

            if (seg.Length >= 2 && seg[0] == '{' && seg[^1] == '}')
            {
                // Named capture {name}
                var name = seg[1..^1];
                if (name.Length == 0)
                    throw new ArgumentException("Empty capture '{}' is not valid.", nameof(pattern));
                captures[name] = pathSegs[pi];
            }
            else if (seg == "*")
            {
                // anonymous single-segment wildcard — no capture
            }
            else if (!string.Equals(seg, pathSegs[pi], StringComparison.Ordinal))
            {
                return PathMatchResult.NoMatch;
            }
        }

        return pi == pathSegs.Length
            ? new PathMatchResult(true, captures)
            : PathMatchResult.NoMatch;
    }
}
