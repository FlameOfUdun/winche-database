namespace Winche.Database.Documents;

/// <summary>
/// Thrown when an <see cref="IndexDefinition.Path"/> violates the collection-path grammar
/// (see <see cref="DocumentPathParser.IsValidIndexPath"/>). Replaces the cryptic
/// "not a valid SQL identifier" ArgumentException consumers previously hit.
/// </summary>
public sealed class InvalidPathPatternException(string path, string reason)
    : Exception($"Invalid index Path '{path}': {reason}")
{
    public string Path { get; } = path;
    public string Reason { get; } = reason;
}
