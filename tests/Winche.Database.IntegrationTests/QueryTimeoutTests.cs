using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Winche.Database.DependencyInjection;
using Winche.Database.Documents;
using Winche.Database.Querying;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Planning;
using Winche.Database.Runtime;
using Winche.Database.Schema;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class QueryTimeoutTests(PostgresFixture fx) : QueryTestBase(fx)
{
    // Anchored on both ends so a match/mismatch can only be settled by exploring the full
    // (a{1,150}){1,150} range; against ~10K seeded characters this costs several CPU-bound
    // seconds server-side with no statement_timeout — calibrated by hand against a real
    // Postgres 17 container (uncapped: ~4.7s; capped at 300ms: aborted at ~300ms).
    private const string RunawayPattern = "^(a{1,150}){1,150}b$";
    private static string RunawayValue => new string('a', 10_000) + "b";

    private async Task<QueryResult> RunTimed(Query query, TimeSpan? timeout)
    {
        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        return await new QueryExecutor(conn, null, statementTimeout: timeout).ExecuteAsync(query);
    }

    [Fact]
    public async Task RunawayRegex_IsAbortedByServer_NearTheConfiguredTimeout()
    {
        await Seed("evil", new StringValue(RunawayValue));
        var query = new Query("c", Where: new FieldFilter(F("f"), FilterOperator.Regex, new StringValue(RunawayPattern)));

        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<RuntimeException>(() => RunTimed(query, TimeSpan.FromMilliseconds(300)));
        sw.Stop();

        Assert.Equal(RuntimeStatus.DeadlineExceeded, ex.Status);
        // Aborted close to the configured timeout, not after the multi-second uncapped runtime —
        // proves the server itself gave up, not merely the client abandoning the wait.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"expected the abort well under 2s, took {sw.Elapsed}");
    }

    [Fact]
    public async Task NormalQuery_WellUnderTimeout_IsUnaffected()
    {
        await Seed("a", new IntegerValue(1));
        await Seed("b", new IntegerValue(2));

        var result = await RunTimed(new Query("c"), TimeSpan.FromSeconds(5));
        Assert.Equal(2, result.Documents.Count);
    }

    [Fact]
    public async Task SchemaInitializationAndIndexCreation_NotSubjectToTimeout()
    {
        // Shorter than any DDL round-trip could possibly complete in — if SchemaManager ever
        // routed through the same bounded path as queries, this would fail with DEADLINE_EXCEEDED.
        var services = new ServiceCollection();
        services.AddWincheDatabase(opts =>
        {
            opts.ConnectionString = Fx.ConnectionString;
            opts.QueryConfig = new QueryConfig { StatementTimeout = TimeSpan.FromTicks(1) };
            opts.UseIndexes(b => b.Add("timeoutcheck", new IndexField("f")));
        });
        await using var provider = services.BuildServiceProvider();

        var schema = provider.GetRequiredService<ISchemaManager>();
        await schema.EnsureCreatedAsync();
        await schema.SyncIndexesAsync();
    }


    [Fact]
    public async Task Timeout_DoesNotLeakToNextConnectionBorrowedFromPool()
    {
        var csb = new NpgsqlConnectionStringBuilder(Fx.ConnectionString) { MaxPoolSize = 1, MinPoolSize = 1 };
        await using var dataSource = NpgsqlDataSource.Create(csb.ConnectionString);

        await Seed("evil", new StringValue(RunawayValue));
        var query = new Query("c", Where: new FieldFilter(F("f"), FilterOperator.Regex, new StringValue(RunawayPattern)));

        await using (var conn1 = await dataSource.OpenConnectionAsync())
        {
            var ex = await Assert.ThrowsAsync<RuntimeException>(() =>
                new QueryExecutor(conn1, null, statementTimeout: TimeSpan.FromMilliseconds(300)).ExecuteAsync(query));
            Assert.Equal(RuntimeStatus.DeadlineExceeded, ex.Status);
        }

        // MaxPoolSize=1 guarantees this is the exact same physical connection reused. If the prior
        // SET LOCAL had outlived its transaction, this would still report the 300ms timeout instead
        // of the server default — proving the setting cannot bleed into whatever borrows the
        // connection next.
        await using (var conn2 = await dataSource.OpenConnectionAsync())
        {
            await using var show = conn2.CreateCommand();
            show.CommandText = "SHOW statement_timeout";
            Assert.Equal("0", (string)(await show.ExecuteScalarAsync())!);
        }
    }
}
