using Npgsql;
using Winche.Database.Constants;
using Winche.Database.Documents;
using Winche.Database.Querying.Sql;
using Winche.Database.Runtime.Writes;
using Winche.Database.Schema;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

/// <summary>
/// Subtree reads and cascade deletes match the path as a byte-order range served by
/// idx_winche_documents_path_c (a LIKE prefix scanned the whole table under the container's en_US collation).
/// </summary>
[Collection("postgres")]
public class SubtreePathIndexTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private static Dictionary<string, Value> Map(int v) => new() { ["v"] = new IntegerValue(v) };

    /// <summary>
    /// The plan Postgres caches for a prepared statement (what Npgsql's prepared or auto-prepared commands
    /// run): PREPARE with the $n placeholders, then EXPLAIN EXECUTE under force_generic_plan, with sequential
    /// scans priced out.
    /// </summary>
    private async Task<string> GenericPlanAsync(CompiledSql compiled)
    {
        static string Literal(object? v) => v switch
        {
            string text => "'" + text.Replace("'", "''") + "'",
            string[] texts => "ARRAY[" + string.Join(", ", texts.Select(Literal)) + "]::text[]",
            _ => throw new NotSupportedException(v?.GetType().Name),
        };

        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        await using (var setup = conn.CreateCommand())
        {
            setup.CommandText = "SET enable_seqscan = off; SET plan_cache_mode = force_generic_plan; " +
                                "DEALLOCATE ALL; PREPARE subtree AS " + compiled.Sql;
            await setup.ExecuteNonQueryAsync();
        }
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "EXPLAIN EXECUTE subtree(" + string.Join(", ", compiled.Parameters.Select(p => Literal(p.Value))) + ")";
        var lines = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync())
            while (await reader.ReadAsync()) lines.Add(reader.GetString(0));
        return string.Join('\n', lines);
    }

    [Fact]
    public async Task The_database_collation_is_not_C()
    {
        // The case the index exists for; if the test container were C, the LIKE plan would have worked too.
        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT datcollate FROM pg_database WHERE datname = current_database()";
        Assert.NotEqual("C", (string)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Subtree_lock_read_and_delete_use_the_path_index()
    {
        foreach (var sql in new[]
        {
            DocumentSql.SelectSubtreeForUpdate("users/u1"),
            DocumentSql.DeleteSubtree("users/u1"),
            WriteSql.LockAll(["other/x"], ["users/u1"]),
        })
        {
            var plan = await GenericPlanAsync(sql);
            // The range (parameters unbound) is an index condition on the path index — not a filter over a
            // whole-table or whole-index scan, which is what the LIKE prefix gets in a generic plan.
            Assert.Contains(SchemaSql.PathIndex, plan);
            Assert.Matches(@"Index Cond: \(\(document_path >= \(\$\d+\)::text\) AND \(document_path < \(\$\d+\)::text\)\)", plan);
            Assert.DoesNotContain("Seq Scan", plan);
            Assert.DoesNotContain("Filter:", plan);
        }
    }

    [Fact]
    public async Task A_cascade_delete_takes_the_subtree_and_nothing_beside_it()
    {
        string[] subtree = ["c/d", "c/d/x/1", "c/d/x/1/y/2", "c/d/z/3"];
        // Siblings that share the prefix "c/d" byte-wise, either side of '/': '-' '.' (below) and '0' 'd' (above).
        string[] beside = ["c/d-", "c/d.", "c/d0", "c/dd", "c/d0/x/1", "c/e", "c/c/d/1"];
        var applier = new WriteApplier(Fx.DataSource);
        await applier.ApplyAsync([.. subtree.Concat(beside).Select((p, i) => new SetWrite { Path = p, Fields = Map(i) })]);

        await applier.ApplyAsync([new DeleteWrite { Path = "c/d", Cascade = true }]);

        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        var left = await new DocumentOperations(conn, null).GetManyAsync([.. subtree, .. beside]);
        Assert.Equal(beside.Order(StringComparer.Ordinal), left.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_cascade_delete_of_a_path_with_like_wildcards_is_literal()
    {
        var applier = new WriteApplier(Fx.DataSource);
        await applier.ApplyAsync([
            new SetWrite { Path = "p/a%", Fields = Map(1) },
            new SetWrite { Path = "p/a%/s/1", Fields = Map(2) },
            new SetWrite { Path = "p/ab/s/1", Fields = Map(3) },
            new SetWrite { Path = "p/a_/s/1", Fields = Map(4) },
        ]);

        await applier.ApplyAsync([new DeleteWrite { Path = "p/a%", Cascade = true }]);

        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        var left = await new DocumentOperations(conn, null).GetManyAsync(["p/a%", "p/a%/s/1", "p/ab/s/1", "p/a_/s/1"]);
        Assert.Equal(["p/a_/s/1", "p/ab/s/1"], left.Keys.Order(StringComparer.Ordinal));
    }

    private async Task<bool?> PathIndexValidAsync(NpgsqlConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT i.indisvalid FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
            WHERE c.relname = '{SchemaSql.PathIndex}'
            """;
        return await cmd.ExecuteScalarAsync() as bool?;
    }

    [Fact]
    public async Task Schema_setup_creates_the_index_and_rebuilds_one_a_failed_build_left_invalid()
    {
        var schema = new SchemaManager(Fx.DataSource, []);
        await using var conn = await Fx.DataSource.OpenConnectionAsync();

        await using (var drop = conn.CreateCommand())
        {
            drop.CommandText = $"DROP INDEX IF EXISTS {SchemaSql.PathIndex}";
            await drop.ExecuteNonQueryAsync();
        }
        await schema.EnsureCreatedAsync();
        Assert.True(await PathIndexValidAsync(conn));

        // What an interrupted CREATE INDEX CONCURRENTLY leaves behind.
        await using (var invalidate = conn.CreateCommand())
        {
            invalidate.CommandText = $"UPDATE pg_index SET indisvalid = false WHERE indexrelid = '{SchemaSql.PathIndex}'::regclass";
            await invalidate.ExecuteNonQueryAsync();
        }
        Assert.False(await PathIndexValidAsync(conn));

        await schema.EnsureCreatedAsync();
        Assert.True(await PathIndexValidAsync(conn));
        await schema.EnsureCreatedAsync();   // and again: idempotent
        Assert.True(await PathIndexValidAsync(conn));
    }
}
