using System.Collections.Immutable;

namespace Winche.Database.Querying;

/// <summary>
/// The result of matching a concrete path against a path pattern.
/// </summary>
public sealed record PathMatchResult(bool IsMatch, IReadOnlyDictionary<string, string> Params)
{
    /// <summary>A singleton representing a failed match.</summary>
    public static PathMatchResult NoMatch { get; } =
        new PathMatchResult(false, ImmutableDictionary<string, string>.Empty);
}
