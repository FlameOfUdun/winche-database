using Winche.Database.Constants;
using Winche.Database.Documents;
using Winche.Database.Querying.Sql;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class IndexSyncTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private readonly PostgresFixture _fx = fx;

    private static readonly IndexDefinition CityIndex = new("c", [new("addr.city")]);

    [Fact]
    public async Task CreateAndDrop_RoundTrip()
    {
        await using var conn = await _fx.DataSource.OpenConnectionAsync();
        await using (var create = conn.CreateCommand())
        {
            create.CommandText = IndexSql.BuildCreate(CityIndex);
            await create.ExecuteNonQueryAsync();
        }

        string discoveredName;
        await using (var check = conn.CreateCommand())
        {
            check.CommandText = $"SELECT indexname FROM pg_indexes WHERE indexname LIKE 'idx_{WincheTables.Documents}_c_%' AND tablename = '{WincheTables.Documents}'";
            var result = await check.ExecuteScalarAsync();
            Assert.NotNull(result);
            discoveredName = (string)result!;
        }

        await using (var drop = conn.CreateCommand())
        {
            drop.CommandText = IndexSql.BuildDrop(discoveredName);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
