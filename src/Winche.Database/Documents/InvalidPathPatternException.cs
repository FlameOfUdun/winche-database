namespace Winche.Database.Documents;

/// <summary>
/// Thrown when an <see cref="IndexDefinition.CollectionId"/> violates the collection-id grammar
/// (see <see cref="DocumentPathParser.IsValidCollectionId"/>). Replaces the cryptic
/// "not a valid SQL identifier" ArgumentException consumers previously hit.
/// </summary>
public sealed class InvalidPathPatternException(string path, string reason)
    : Exception($"Invalid index CollectionId '{path}': {reason}")
{
    public string Path { get; } = path;
    public string Reason { get; } = reason;
}
