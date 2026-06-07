using Npgsql;
using Testcontainers.PostgreSql;
using Winche.Database.Querying.Sql;

namespace Winche.Database.IntegrationTests;

/// <summary>One Postgres container for the whole test run; schema installed once.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .Build();

    public NpgsqlDataSource DataSource { get; private set; } = null!;
    public string Table => "documents";

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        DataSource = NpgsqlDataSource.Create(_container.GetConnectionString());

        await using var conn = await DataSource.OpenConnectionAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = SchemaSql.TableDdl(Table);
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = SchemaSql.HelperFunctions();
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = SchemaSql.ChangesDdl(Table);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }

    /// <summary>Call at the start of each test class for isolation.</summary>
    public async Task ResetAsync()
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"TRUNCATE {Table}";
        await cmd.ExecuteNonQueryAsync();
        await ResetChangesAsync();
    }

    /// <summary>Reads all change rows after seq, in order.</summary>
    public async Task<List<(long Seq, string Type, string Path, string Collection, long Version, DateTimeOffset CommitTime)>>
        ReadChangesAsync(long afterSeq = 0)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT seq, type, path, collection, version, commit_time FROM {Table}_changes WHERE seq > $1 ORDER BY seq";
        cmd.Parameters.AddWithValue(afterSeq);
        var rows = new List<(long, string, string, string, long, DateTimeOffset)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                      reader.GetInt64(4), reader.GetFieldValue<DateTimeOffset>(5)));
        return rows;
    }

    public async Task ResetChangesAsync()
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"TRUNCATE {Table}_changes RESTART IDENTITY";
        await cmd.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
