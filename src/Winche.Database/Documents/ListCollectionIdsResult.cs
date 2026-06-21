namespace Winche.Database.Documents;

/// <summary>
/// Result of <c>DocumentDatabase.ListCollectionIdsAsync</c>:
/// the distinct, lexicographically-ordered collection ids for one page,
/// plus an opaque <see cref="NextPageToken"/> (null when there are no more pages).
/// </summary>
public sealed record ListCollectionIdsResult(
    IReadOnlyList<string> CollectionIds,
    string? NextPageToken);
