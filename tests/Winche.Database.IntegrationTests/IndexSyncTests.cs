using Winche.Database.Constants;
using Winche.Database.Documents;
using Winche.Database.Querying.Sql;
using Winche.Database.Values;

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

    /// <summary>
    /// Regression: CREATE INDEX evaluates the winche_key() expression on existing rows, and during
    /// an index build PostgreSQL runs functions under a hardened search_path that excludes the
    /// install schema (CVE-2018-1058). winche_key calls sibling winche_* functions by unqualified
    /// name, so without its SET search_path clause the build fails with
    /// "function winche_rank(jsonb) does not exist". The empty-table round-trip above never hits
    /// this — winche_key (STRICT) only runs when the indexed field is actually present in a row.
    /// </summary>
    [Fact]
    public async Task CreateIndex_OnPopulatedTable_EvaluatesWincheKey()
    {
        await Seed("doc1", new StringValue("oslo")); // field "f" present -> winche_key body runs

        var index = new IndexDefinition("c", [new("f")]);
        var createSql = IndexSql.BuildCreate(index);
        await using var conn = await _fx.DataSource.OpenConnectionAsync();
        await using (var create = conn.CreateCommand())
        {
            create.CommandText = createSql;
            await create.ExecuteNonQueryAsync(); // threw "winche_rank(jsonb) does not exist" pre-fix
        }

        // Tidy up so the partial index does not linger on the shared container.
        var indexName = createSql.Split('"')[1];
        await using var drop = conn.CreateCommand();
        drop.CommandText = IndexSql.BuildDrop(indexName);
        await drop.ExecuteNonQueryAsync();
    }
}
