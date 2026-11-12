using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Winche.Database.Constants;
using Winche.Database.DependencyInjection;
using Winche.Database.Documents;
using Winche.Database.Querying;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Planning;
using Winche.Database.Querying.Sql;
using Winche.Database.Runtime.Writes;
using Winche.Database.Schema;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

/// <summary>
/// 10.5.0 index management: declared indexes built without blocking writes, invalid ones rebuilt, ones no
/// longer declared dropped; the unused whole-document GIN index dropped; array-membership indexes; and
/// slow-query index advice.
/// </summary>
[Collection("postgres")]
public class IndexManagementTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private async Task<Dictionary<string, bool>> IndexesAsync()
    {
        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT c.relname, i.indisvalid FROM pg_index i
            JOIN pg_class c ON c.oid = i.indexrelid JOIN pg_class t ON t.oid = i.indrelid
            WHERE t.relname = '{WincheDatabaseTables.Documents}'
            """;
        var result = new Dictionary<string, bool>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result[reader.GetString(0)] = reader.GetBoolean(1);
        return result;
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private SchemaManager Schema(IEnumerable<IndexDefinition> indexes, bool dropUndeclared = true) =>
        new(Fx.DataSource, indexes, Options.Create(new WincheDatabaseOptions { DropUndeclaredIndexes = dropUndeclared }));

    [Fact]
    public async Task Startup_drops_the_unused_whole_document_index()
    {
        await ExecAsync($"CREATE INDEX IF NOT EXISTS idx_{WincheDatabaseTables.Documents}_data ON {WincheDatabaseTables.Documents} USING GIN(data)");
        Assert.Contains($"idx_{WincheDatabaseTables.Documents}_data", (await IndexesAsync()).Keys);

        await Schema([]).EnsureCreatedAsync();

        Assert.DoesNotContain($"idx_{WincheDatabaseTables.Documents}_data", (await IndexesAsync()).Keys);
    }

    [Fact]
    public async Task Sync_builds_declared_indexes_rebuilds_invalid_ones_and_drops_undeclared_ones()
    {
        await Seed("x", new StringValue("oslo"));   // a populated table: the index functions run during builds
        var keep = new IndexDefinition("c", [new("f")]);
        var tags = new IndexDefinition("c", [new("tags")], Kind: IndexKind.ArrayMembership);
        var removed = new IndexDefinition("c", [new("old"), new("f", SortDirection.Desc)]);
        await ExecAsync($"CREATE INDEX IF NOT EXISTS mine ON {WincheDatabaseTables.Documents} (document_id)");   // not ours

        await Schema([keep, tags, removed]).SyncIndexesAsync();
        var all = await IndexesAsync();
        Assert.True(all[IndexSql.NameOf(keep)] && all[IndexSql.NameOf(tags)] && all[IndexSql.NameOf(removed)]);

        // An interrupted CONCURRENTLY build leaves the index invalid: the next sync rebuilds it.
        await ExecAsync($"UPDATE pg_index SET indisvalid = false WHERE indexrelid = '\"{IndexSql.NameOf(keep)}\"'::regclass");
        await Schema([keep, tags, removed]).SyncIndexesAsync();
        Assert.True((await IndexesAsync())[IndexSql.NameOf(keep)]);

        // A definition removed from the code: its index goes; built-in and hand-made indexes stay.
        await Schema([keep, tags]).SyncIndexesAsync();
        all = await IndexesAsync();
        Assert.DoesNotContain(IndexSql.NameOf(removed), all.Keys);
        Assert.Contains(IndexSql.NameOf(keep), all.Keys);
        Assert.Contains("mine", all.Keys);
        Assert.Contains(SchemaSql.PathIndex, all.Keys);
        Assert.Contains($"{WincheDatabaseTables.Documents}_pkey", all.Keys);

        // Opted out: nothing is dropped.
        await Schema([], dropUndeclared: false).SyncIndexesAsync();
        Assert.Contains(IndexSql.NameOf(keep), (await IndexesAsync()).Keys);

        await Schema([]).SyncIndexesAsync();   // leave nothing behind for other tests
        await ExecAsync("DROP INDEX IF EXISTS mine");
    }

    [Fact]
    public void Ordered_index_names_are_unchanged_and_array_ones_differ()
    {
        // Existing deployments keep their index names (no rebuild on upgrade).
        var ordered = new IndexDefinition("sessionHistory", [new("runtimeType"), new("finishedAt", SortDirection.Desc)]);
        Assert.Equal(IndexSql.NameOf(ordered), IndexSql.NameOf(ordered with { }));
        Assert.NotEqual(IndexSql.NameOf(new IndexDefinition("c", [new("tags")])),
            IndexSql.NameOf(new IndexDefinition("c", [new("tags")], Kind: IndexKind.ArrayMembership)));
        Assert.Contains("CREATE INDEX CONCURRENTLY IF NOT EXISTS", IndexSql.BuildCreate(ordered, concurrently: true));
        Assert.Throws<ArgumentException>(() => IndexSql.BuildCreate(new IndexDefinition("c", [new("a"), new("b")], Kind: IndexKind.ArrayMembership)));
    }

    // ── array-membership indexes ─────────────────────────────────────────────

    private static readonly Value[] Elements =
    [
        new IntegerValue(1), new DoubleValue(1.0), new IntegerValue(2), new DoubleValue(2.5), new StringValue("a"),
        new StringValue("b"), new NullValue(), new BooleanValue(true),
        new ArrayValue([new IntegerValue(1)]),
        new MapValue(new Dictionary<string, Value> { ["k"] = new StringValue("v") }),
    ];

    private async Task SeedArraysAsync(Random rng, int count)
    {
        var writes = new List<Write>();
        for (var i = 0; i < count; i++)
        {
            var fields = new Dictionary<string, Value>();
            if (rng.Next(8) > 0)
                fields["tags"] = rng.Next(10) == 0
                    ? new StringValue("not an array")
                    : new ArrayValue([.. Enumerable.Range(0, rng.Next(0, 4)).Select(_ => Elements[rng.Next(Elements.Length)])]);
            writes.Add(new SetWrite { Path = $"arr/d{i}", Fields = fields });
        }
        await new WriteApplier(Fx.DataSource).ApplyAsync(writes);
    }

    private async Task<List<string>> PathsAsync(Query query, CollectionIndexResolver? scopes)
    {
        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        return [.. (await new QueryExecutor(conn, null, scopes).ExecuteAsync(query)).Documents.Select(d => d.Path)];
    }

    private static readonly FieldPath Tags = FieldPath.Parse("tags");

    private static IEnumerable<Query> ArrayQueries()
    {
        foreach (var e in Elements)
            yield return new Query("arr", Where: new FieldFilter(Tags, FilterOperator.ArrayContains, e), Limit: 500);
        yield return new Query("arr", Where: new FieldFilter(Tags, FilterOperator.ArrayContainsAny, new ArrayValue([new IntegerValue(2), new StringValue("b")])), Limit: 500);
        yield return new Query("arr", Where: new FieldFilter(Tags, FilterOperator.ArrayContainsAll, new ArrayValue([new DoubleValue(1.0), new StringValue("a")])), Limit: 500);
        yield return new Query("arr", Where: new CompositeFilter(CompositeOp.And, [
            new FieldFilter(Tags, FilterOperator.ArrayContains, new IntegerValue(1)),
            new UnaryFilter(Tags, UnaryOp.Exists)]), Limit: 500);
        yield return new Query("arr", Where: new CompositeFilter(CompositeOp.Not, [
            new FieldFilter(Tags, FilterOperator.ArrayContains, new IntegerValue(1))]), Limit: 500);
    }

    [Fact]
    public async Task An_array_index_returns_exactly_what_the_query_returns_without_it_and_is_used()
    {
        await SeedArraysAsync(new Random(7), 300);
        var index = new IndexDefinition("arr", [new("tags")], Kind: IndexKind.ArrayMembership);
        await Schema([index]).SyncIndexesAsync();
        var indexed = new CollectionIndexResolver([index]);

        foreach (var query in ArrayQueries())
            Assert.Equal(await PathsAsync(query, scopes: null), await PathsAsync(query, indexed));

        // The plan uses the GIN index (sequential scans priced out so a small table doesn't hide it).
        var contains = new Query("arr", Where: new FieldFilter(Tags, FilterOperator.ArrayContains, new StringValue("a")), Limit: 500);
        var plan = indexed.WithArrayIndexConditions("arr", Normalizer.Normalize(contains));
        var compiled = SqlCompiler.Compile(plan, indexed.ScopeFor("arr"), null);
        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        await using (var off = conn.CreateCommand()) { off.CommandText = "SET enable_seqscan = off"; await off.ExecuteNonQueryAsync(); }
        await using var explain = conn.CreateCommand();
        explain.CommandText = "EXPLAIN " + compiled.Sql;
        explain.Parameters.AddRange(compiled.Parameters);
        var lines = new List<string>();
        await using (var reader = await explain.ExecuteReaderAsync())
            while (await reader.ReadAsync()) lines.Add(reader.GetString(0));
        Assert.Contains(lines, l => l.Contains(IndexSql.NameOf(index)));

        await Schema([]).SyncIndexesAsync();
    }

    // ── advice ─────────────────────────────────────────────────────────────

    private static string? Advise(CollectionIndexResolver r, Query q) => r.Advise(q, Normalizer.Normalize(q));

    private static FieldPath F(string p) => FieldPath.Parse(p);

    [Fact]
    public void A_suggestion_is_the_index_itself_and_serves_the_query_once_declared()
    {
        var byType = new Query("userData/u1/sessionHistory",
            Where: new FieldFilter(F("runtimeType"), FilterOperator.Eq, new StringValue("airway")),
            OrderBy: [new Ordering(F("finishedAt"), SortDirection.Desc)]);
        var s = new CollectionIndexResolver([]).SuggestionFor(byType, Normalizer.Normalize(byType))!;
        Assert.Equal(("sessionHistory", IndexKind.Ordered, false), (s.CollectionId, s.Kind, s.CollectionGroup));
        Assert.Equal([new IndexField("runtimeType"), new IndexField("finishedAt", SortDirection.Desc)], s.Fields);
        Assert.Null(Advise(new CollectionIndexResolver([s.ToDefinition()]), byType));
    }

    [Fact]
    public void Advice_names_the_index_a_query_needs_and_nothing_when_one_serves_it()
    {
        var none = new CollectionIndexResolver([]);
        var byType = new Query("userData/u1/sessionHistory",
            Where: new FieldFilter(F("runtimeType"), FilterOperator.Eq, new StringValue("airway")),
            OrderBy: [new Ordering(F("finishedAt"), SortDirection.Desc)]);

        Assert.Equal("""ib.Add("sessionHistory", [new("runtimeType"), new("finishedAt", SortDirection.Desc)])""", Advise(none, byType));
        Assert.Null(Advise(new CollectionIndexResolver([new("sessionHistory", [new("runtimeType"), new("finishedAt", SortDirection.Desc)])]), byType));
        Assert.Null(Advise(new CollectionIndexResolver([new("sessionHistory", [new("runtimeType"), new("finishedAt")])]), byType));   // read backwards
        Assert.NotNull(Advise(new CollectionIndexResolver([new("sessionHistory", [new("finishedAt"), new("runtimeType")])]), byType)); // wrong order

        Assert.Equal("""ib.Add("c", [new("n")])""",
            Advise(none, new Query("c", Where: new FieldFilter(F("n"), FilterOperator.Gt, new IntegerValue(1)))));
        Assert.Equal("""ib.AddArray("c", "tags")""",
            Advise(none, new Query("c", Where: new FieldFilter(F("tags"), FilterOperator.ArrayContains, new StringValue("a")))));
        Assert.Null(Advise(none, new Query("c")));   // a plain collection read: the built-in index serves it
    }

    private sealed class CapturingLogger : ILogger<CollectionIndexResolver>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    [Fact]
    public async Task A_slow_query_without_an_index_logs_the_advice_once()
    {
        var logger = new CapturingLogger();
        var resolver = new CollectionIndexResolver([], logger,
            Options.Create(new WincheDatabaseOptions { SlowQueryThreshold = TimeSpan.Zero }));
        var query = new Query("c", OrderBy: [new Ordering(F("n"))]);

        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        await new QueryExecutor(conn, null, resolver).ExecuteAsync(query);
        await new QueryExecutor(conn, null, resolver).ExecuteAsync(query);
        await new QueryExecutor(conn, null, resolver).CountAsync(query);

        var message = Assert.Single(logger.Messages);
        Assert.Contains("""ib.Add("c", [new("n")])""", message);

        var quiet = new CapturingLogger();
        await new QueryExecutor(conn, null, new CollectionIndexResolver([], quiet,
            Options.Create(new WincheDatabaseOptions { SlowQueryThreshold = null }))).ExecuteAsync(query);
        Assert.Empty(quiet.Messages);
    }
}
