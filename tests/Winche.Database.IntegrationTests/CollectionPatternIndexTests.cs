using System.Text;
using System.Text.RegularExpressions;
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
        new("userData/*/sessionHistory", [new("startedAt", SortDirection.Desc)]);

    private static IPathPatternMatcher Matcher() => PathPatternMatcher.Instance;

    [Theory]
    [InlineData("userData/*/sessionHistory", "userData/u1/sessionHistory", true)]
    [InlineData("userData/*/sessionHistory", "adminData/a1/sessionHistory", false)]
    [InlineData("userData/*/sessionHistory", "userData/u1/sessionHistory/x", false)]
    [InlineData("orgs/acme/teams/*/members", "orgs/acme/teams/t1/members", true)]
    [InlineData("orgs/acme/teams/*/members", "orgs/globex/teams/t1/members", false)]
    public async Task Parity_Matcher_CsRegex_PgRegex_Agree(string pattern, string path, bool expected)
    {
        var rx = DocumentPathParser.CollectionPatternRegex(pattern);
        Assert.Equal(expected, Matcher().Match(pattern, path).IsMatch);
        Assert.Equal(expected, Regex.IsMatch(path, rx));

        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT @p ~ @r";
        cmd.Parameters.AddWithValue("p", path);
        cmd.Parameters.AddWithValue("r", rx);
        Assert.Equal(expected, (bool)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task PatternIndex_UsedByQuery_AndIsolatesParents()
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

        var resolver = new IndexScopeResolver([SessionHistoryIndex], Matcher());
        var scope = resolver.ScopeRegexes("userData/alice/sessionHistory");
        Assert.NotEmpty(scope);

        // The pattern index is usable for the real compiled SQL on the query path. (Natural selection at
        // scale is proven separately; here enable_seqscan=off isolates index *usability* on a tiny corpus.)
        await using (var seqoff = conn.CreateCommand())
        {
            seqoff.CommandText = "SET enable_seqscan = off";
            await seqoff.ExecuteNonQueryAsync();
        }

        // QUERY path uses the index
        var q = new Query("userData/alice/sessionHistory",
            OrderBy: [new Ordering(FieldPath.Parse("startedAt"), SortDirection.Desc)]);
        var qPlan = await ExplainAsync(conn, SqlCompiler.Compile(Normalizer.Normalize(q), scope));
        Assert.Contains("idx_winche_documents_userData", qPlan);
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
