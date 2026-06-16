using System.Text;
using Npgsql;
using Winche.Database.Documents;
using Winche.Database.Querying;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Planning;
using Winche.Database.Querying.Sql;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class CollectionPatternIndexTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private static readonly IndexDefinition SessionHistoryIndex =
        new("sessionHistory", [new("startedAt", SortDirection.Desc)]);

    [Fact]
    public async Task CollectionIdIndex_UsedByQuery_AndIsolatesParents()
    {
        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        await using (var create = conn.CreateCommand())
        {
            create.CommandText = IndexSql.BuildCreate(SessionHistoryIndex);
            await create.ExecuteNonQueryAsync();
        }

        await SeedDoc("s1", new() { ["startedAt"] = new IntegerValue(10) }, "userData/alice/sessionHistory");
        await SeedDoc("s2", new() { ["startedAt"] = new IntegerValue(20) }, "userData/alice/sessionHistory");
        await SeedDoc("s3", new() { ["startedAt"] = new IntegerValue(30) }, "userData/bob/sessionHistory");
        await SeedDoc("s4", new() { ["startedAt"] = new IntegerValue(40) }, "adminData/root/sessionHistory");

        // isolation: per-user query returns only alice's two docs
        var result = await Run(new Query("userData/alice/sessionHistory",
            OrderBy: [new Ordering(FieldPath.Parse("startedAt"), SortDirection.Desc)]));
        Assert.Equal(
            new[] { "userData/alice/sessionHistory/s2", "userData/alice/sessionHistory/s1" },
            result.Documents.Select(d => d.Path).ToArray());

        // bob's collection returns only bob's doc
        var bobResult = await Run(new Query("userData/bob/sessionHistory",
            OrderBy: [new Ordering(FieldPath.Parse("startedAt"), SortDirection.Desc)]));
        Assert.Equal(
            new[] { "userData/bob/sessionHistory/s3" },
            bobResult.Documents.Select(d => d.Path).ToArray());

        var resolver = new CollectionIndexResolver([SessionHistoryIndex]);
        var aliceScope = resolver.ScopeFor("userData/alice/sessionHistory");
        Assert.Equal("sessionHistory", aliceScope);
        var bobScope = resolver.ScopeFor("userData/bob/sessionHistory");
        Assert.Equal("sessionHistory", bobScope);

        // The collection-ID partial index is usable for the real compiled SQL on the query path.
        // (Natural selection at scale is proven separately; here enable_seqscan=off isolates
        // index *usability* on a tiny corpus.)
        await using (var seqoff = conn.CreateCommand())
        {
            seqoff.CommandText = "SET enable_seqscan = off";
            await seqoff.ExecuteNonQueryAsync();
        }

        // QUERY path uses the index
        var q = new Query("userData/alice/sessionHistory",
            OrderBy: [new Ordering(FieldPath.Parse("startedAt"), SortDirection.Desc)]);
        var qPlan = await ExplainAsync(conn, SqlCompiler.Compile(Normalizer.Normalize(q), aliceScope));
        Assert.Contains("idx_winche_documents_sessionHistory", qPlan);
    }

    [Fact]
    public async Task CollectionIdIndex_BothParentsQueryCorrectly()
    {
        await SeedDoc("s1", new() { ["startedAt"] = new IntegerValue(10) }, "userData/alice/sessionHistory");
        await SeedDoc("s3", new() { ["startedAt"] = new IntegerValue(30) }, "userData/bob/sessionHistory");

        // Each parent collection returns only its own docs
        var aliceResult = await Run(new Query("userData/alice/sessionHistory"));
        Assert.Single(aliceResult.Documents);
        Assert.Equal("userData/alice/sessionHistory/s1", aliceResult.Documents[0].Path);

        var bobResult = await Run(new Query("userData/bob/sessionHistory"));
        Assert.Single(bobResult.Documents);
        Assert.Equal("userData/bob/sessionHistory/s3", bobResult.Documents[0].Path);
    }

    private static async Task<string> ExplainAsync(NpgsqlConnection conn, CompiledSql compiled)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "EXPLAIN " + compiled.Sql;
        foreach (var p in compiled.Parameters)
            cmd.Parameters.AddWithValue(p.ParameterName, p.Value!);
        var sb = new StringBuilder();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) sb.AppendLine(r.GetString(0));
        return sb.ToString();
    }
}
