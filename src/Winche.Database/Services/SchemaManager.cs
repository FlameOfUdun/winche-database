using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Winche.Database.Constants;
using Winche.Database.Documents;
using Winche.Database.Interfaces;
using Winche.Database.Models;
using Winche.Database.Querying.Sql;

namespace Winche.Database.Services;

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

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = SchemaSql.TableDdl(_table);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = SchemaSql.HelperFunctions(_schema);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task SyncIndexesAsync(CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);

        foreach (var index in _indexes)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = IndexSql.BuildCreate(index, _schema, _table);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task DropIndexAsync(string indexName, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = IndexSql.BuildDrop(_schema, indexName);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
