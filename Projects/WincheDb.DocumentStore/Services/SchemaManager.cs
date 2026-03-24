using Npgsql;
using WincheDb.SqlBuilder;

namespace WincheDb.DocumentStore.Services;

public sealed class SchemaManager(
    NpgsqlDataSource source, 
    StoreOptions options
)
{
    public async Task EnsureCreatedAsync(CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = new TableSqlBuilder().Build();
        await cmd.ExecuteNonQueryAsync(ct);
    }


    public async Task SyncIndexesAsync(IEnumerable<IndexDefinition> indexes, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);

        foreach (var index in indexes)
        {
            await using var cmd = conn.CreateCommand();
            IndexSqlBuilder.BuildCreate(index, options.Schema, options.TableName).Apply(cmd);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task DropIndexAsync(string indexName, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = IndexSqlBuilder.BuildDrop(options.Schema, indexName);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}