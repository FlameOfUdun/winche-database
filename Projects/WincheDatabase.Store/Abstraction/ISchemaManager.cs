using WincheDatabase.SQL;

namespace WincheDatabase.Store.Abstraction;

public interface ISchemaManager
{
    Task EnsureCreatedAsync(CancellationToken ct = default);
    Task SyncIndexesAsync(IEnumerable<IndexDefinition> indexes, CancellationToken ct = default);
    Task DropIndexAsync(string indexName, CancellationToken ct = default);
}
