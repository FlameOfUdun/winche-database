using Npgsql;
using Testcontainers.PostgreSql;
using Winche.Database.Constants;
using Winche.Database.Querying.Sql;

namespace Winche.Database.IntegrationTests;

/// <summary>One Postgres container for the whole test run; schema installed once.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .Build();

    public NpgsqlDataSource DataSource { get; private set; } = null!;
    /// <summary>Raw connection string captured at startup — safe even if NpgsqlDataSource.ConnectionString is unavailable.</summary>
    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
        DataSource = NpgsqlDataSource.Create(ConnectionString);

        await using var conn = await DataSource.OpenConnectionAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = SchemaSql.TableDdl();
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = SchemaSql.HelperFunctions();
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = SchemaSql.ChangesDdl();
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
        cmd.CommandText = $"TRUNCATE {WincheTables.Documents}";
        await cmd.ExecuteNonQueryAsync();
        await ResetChangesAsync();
        await using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = $"TRUNCATE {WincheTables.FeedCursors}";
        await cmd2.ExecuteNonQueryAsync();
    }

    /// <summary>Reads all change rows after seq, in order.</summary>
    public async Task<List<(long Seq, string Type, string Path, string Collection, long Version, DateTimeOffset CommitTime)>>
        ReadChangesAsync(long afterSeq = 0)
    {
        await using var conn = await DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT seq, type, path, collection, version, commit_time FROM {WincheTables.Changes} WHERE seq > $1 ORDER BY seq";
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
        cmd.CommandText = $"TRUNCATE {WincheTables.Changes} RESTART IDENTITY";
        await cmd.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
