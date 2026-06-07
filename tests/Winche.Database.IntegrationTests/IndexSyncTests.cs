using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Sql;

namespace Winche.Database.IntegrationTests;

file sealed class CityIndex : IndexDefinition
{
    public override string Collection => "c";
    public override IReadOnlyList<IndexField> Fields => [new("addr.city")];
}

[Collection("postgres")]
public class IndexSyncTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private readonly PostgresFixture _fx = fx;

    [Fact]
    public async Task CreateAndDrop_RoundTrip()
    {
        await using var conn = await _fx.DataSource.OpenConnectionAsync();
        await using (var create = conn.CreateCommand())
        {
            create.CommandText = IndexSql.BuildCreate(new CityIndex(), "public", _fx.Table);
            await create.ExecuteNonQueryAsync();
        }

        string discoveredName;
        await using (var check = conn.CreateCommand())
        {
            check.CommandText = $"SELECT indexname FROM pg_indexes WHERE indexname LIKE 'idx_{_fx.Table}_c_%' AND tablename = '{_fx.Table}'";
            var result = await check.ExecuteScalarAsync();
            Assert.NotNull(result);
            discoveredName = (string)result!;
        }

        await using (var drop = conn.CreateCommand())
        {
            drop.CommandText = IndexSql.BuildDrop("public", discoveredName);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
