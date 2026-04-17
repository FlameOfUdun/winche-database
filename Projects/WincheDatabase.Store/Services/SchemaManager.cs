using Microsoft.Extensions.Options;
using Npgsql;
using WincheDatabase.SQL;
using WincheDatabase.Store;

namespace WincheDatabase.Store.Services;

public sealed class SchemaManager(
    NpgsqlDataSource source, 
    IOptions<StoreOptions> options
)
{
    private readonly string _table = options.Value.TableName;
    private readonly string _schema = options.Value.Schema;

    public async Task EnsureCreatedAsync(CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = new TableSqlBuilder(_table).Build();
        await cmd.ExecuteNonQueryAsync(ct);
    }


    public async Task SyncIndexesAsync(IEnumerable<IndexDefinition> indexes, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);

        foreach (var index in indexes)
        {
            await using var cmd = conn.CreateCommand();
            IndexSqlBuilder.BuildCreate(index, _schema, _table).Apply(cmd);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task DropIndexAsync(string indexName, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = IndexSqlBuilder.BuildDrop(_schema, indexName);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}