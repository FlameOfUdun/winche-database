using Winche.Database.Documents;

namespace Winche.Database.Querying;

/// <summary>
/// Decides the collection_id predicate to add to a query so Postgres can use a declared
/// collection-ID partial index. Returns the collection's id iff an index covers it, else null.
/// </summary>
public sealed class CollectionIndexResolver(IEnumerable<IndexDefinition> indexes)
{
    private readonly HashSet<string> _declaredCollectionIds = indexes.Select(i => i.CollectionId).ToHashSet(StringComparer.Ordinal);

    public string? ScopeFor(string collectionPath)
    {
        var id = DocumentPathParser.CollectionIdOf(collectionPath);
        return _declaredCollectionIds.Contains(id) ? id : null;
    }
}
