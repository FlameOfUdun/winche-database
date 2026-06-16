using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Winche.Database.Constants;
using Winche.Database.Documents;
using Winche.Database.Querying.Sql;

namespace Winche.Database.Schema;

public sealed class SchemaManager(
    [FromKeyedServices(ServiceKeys.DATA_SOURCE_KEY)] NpgsqlDataSource source,
    IEnumerable<IndexDefinition> indexes
) : ISchemaManager
{
    private readonly IEnumerable<IndexDefinition> _indexes = indexes;

    public async Task EnsureCreatedAsync(CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);

        // Run the idempotent legacy→current migration FIRST, before the additive CREATE-IF-NOT-EXISTS
        // DDL — otherwise the additive DDL would create new-named objects alongside the legacy ones.
        // No-op on a fresh or already-migrated database.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = SchemaSql.MigrationDdl();
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = SchemaSql.TableDdl();
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = SchemaSql.HelperFunctions();
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = SchemaSql.ChangesDdl();
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task SyncIndexesAsync(CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);

        foreach (var index in _indexes)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = IndexSql.BuildCreate(index);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task DropIndexAsync(string indexName, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = IndexSql.BuildDrop(indexName);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
