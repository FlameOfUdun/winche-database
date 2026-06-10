namespace Winche.Database.Querying;

/// <summary>
/// Matches concrete paths against path patterns. Patterns may use literal segments, <c>*</c>
/// (single-segment wildcard), <c>**</c> (multi-segment trailing wildcard), and <c>{name}</c>
/// named single-segment captures.
/// </summary>
public interface IPathPatternMatcher
{
    /// <summary>
    /// Matches <paramref name="path"/> against <paramref name="pattern"/>.
    /// </summary>
    PathMatchResult Match(string pattern, string path);
}
