using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using WincheDatabase.AST.Models;
using WincheDatabase.SQL;
using WincheDatabase.Store.Abstraction;
using WincheDatabase.Store.Constants;
using WincheDatabase.Store.Models;

namespace WincheDatabase.Store.Services;

public sealed class SchemaManager(
    [FromKeyedServices(ServiceKeys.DATA_SOURCE_KEY)] NpgsqlDataSource source,
    IOptions<StoreOptions> options,
    IEnumerable<IndexDefinition> indexes
) : ISchemaManager
{
    private readonly string _table = options.Value.TableName;
    private readonly string _schema = options.Value.Schema;
    private readonly IEnumerable<IndexDefinition> _indexes = indexes;

    public async Task EnsureCreatedAsync(CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = new TableSqlBuilder(_table).Build();
        await cmd.ExecuteNonQueryAsync(ct);
    }


    public async Task SyncIndexesAsync(CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);

        foreach (var index in _indexes)
        {
            if (index.Fields.Count == 0)
                throw new InvalidOperationException("Index must have at least one field.");

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