namespace Winche.Database.Interfaces;

public interface ISchemaManager
{
    Task EnsureCreatedAsync(CancellationToken ct = default);
    Task SyncIndexesAsync(CancellationToken ct = default);
    Task DropIndexAsync(string indexName, CancellationToken ct = default);
}
