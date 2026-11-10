using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Winche.Database.DependencyInjection;
using Winche.Database.Constants;
using Winche.Database.Documents;
using Winche.Database.Querying.Sql;

namespace Winche.Database.Schema;

public sealed class SchemaManager(
    [FromKeyedServices(WincheDatabaseKeys.DataSource)] NpgsqlDataSource source,
    IEnumerable<IndexDefinition> indexes,
    IOptions<WincheDatabaseOptions>? options = null,
    ILogger<SchemaManager>? logger = null
) : ISchemaManager
{
    private readonly IReadOnlyList<IndexDefinition> _indexes = indexes.ToList();
    private readonly bool _dropUndeclared = options?.Value.DropUndeclaredIndexes ?? true;

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

        await EnsurePathIndexAsync(conn, ct);

        await using (var drop = conn.CreateCommand())
        {
            drop.CommandText = SchemaSql.DropUnusedDataIndex();
            await drop.ExecuteNonQueryAsync(ct);
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

    private static async Task EnsurePathIndexAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using (var check = conn.CreateCommand())
        {
            check.CommandText = SchemaSql.PathIndexIsInvalid();
            if (await check.ExecuteScalarAsync(ct) is true)
            {
                await using var drop = conn.CreateCommand();
                drop.CommandText = SchemaSql.DropPathIndex();
                await drop.ExecuteNonQueryAsync(ct);
            }
        }

        await using var create = conn.CreateCommand();
        create.CommandText = SchemaSql.CreatePathIndex();
        create.CommandTimeout = 0;   // a first build on a large table can take a while; it doesn't block writes
        await create.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Builds every declared index that is missing, without blocking writes (CONCURRENTLY), replacing any a
    /// failed build left invalid; then, unless <see cref="WincheDatabaseOptions.DropUndeclaredIndexes"/> is off,
    /// drops the indexes this library made for definitions that are no longer declared.
    /// </summary>
    public async Task SyncIndexesAsync(CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);

        var existing = new Dictionary<string, bool>(StringComparer.Ordinal);   // name → valid
        await using (var list = conn.CreateCommand())
        {
            list.CommandText = IndexSql.ListManaged();
            await using var reader = await list.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) existing[reader.GetString(0)] = reader.GetBoolean(1);
        }

        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var index in _indexes)
        {
            var name = IndexSql.NameOf(index);
            declared.Add(name);
            if (existing.TryGetValue(name, out var valid))
            {
                if (valid) continue;
                await ExecuteAsync(conn, IndexSql.BuildDropConcurrently(name), ct);   // left by an interrupted build
            }
            logger?.LogInformation("Building index {Index}", name);
            await ExecuteAsync(conn, IndexSql.BuildCreate(index, concurrently: true), ct);
        }

        if (!_dropUndeclared) return;
        foreach (var name in existing.Keys.Where(n => !declared.Contains(n)))
        {
            logger?.LogInformation("Dropping index {Index}: no longer declared", name);
            await ExecuteAsync(conn, IndexSql.BuildDropConcurrently(name), ct);
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0;   // a build on a large table can take a while; CONCURRENTLY doesn't block writes
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DropIndexAsync(string indexName, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = IndexSql.BuildDrop(indexName);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
