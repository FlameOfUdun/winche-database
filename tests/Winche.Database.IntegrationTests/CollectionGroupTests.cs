using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Winche.Database.Authorization;
using Winche.Database.DependencyInjection;
using Winche.Database.Documents;
using Winche.Database.Querying;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Ast.Serialization;
using Winche.Database.Querying.Planning;
using Winche.Database.Querying.Sql;
using Winche.Database.Runtime;
using Winche.Database.Runtime.ChangeFeed;
using Winche.Database.Runtime.Listening;
using Winche.Database.Runtime.Writes;
using Winche.Database.Schema;
using Winche.Database.Values;
using Winche.Rules;
using Winche.Rules.Text;

namespace Winche.Database.IntegrationTests;

/// <summary>Collection group queries (10.5.0): every collection with an id, under any parent.</summary>
[Collection("postgres")]
public class CollectionGroupTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private DocumentDatabase Core(QueryListenerRegistry? listeners = null, CollectionIndexResolver? scopes = null) =>
        new(Fx.DataSource, Options.Create(new WincheDatabaseOptions()), listeners, scopes);

    private static Dictionary<string, Value> Note(string owner, int n) => new() { ["owner"] = new StringValue(owner), ["n"] = new IntegerValue(n) };

    private async Task SeedAsync()
    {
        await Core().WriteAsync([
            new SetWrite { Path = "users/u1/notes/a", Fields = Note("u1", 3) },
            new SetWrite { Path = "users/u1/notes/b", Fields = Note("u1", 1) },
            new SetWrite { Path = "users/u2/notes/c", Fields = Note("u2", 2) },
            new SetWrite { Path = "teams/t1/notes/d", Fields = Note("t1", 5) },
            new SetWrite { Path = "notes/top", Fields = Note("x", 4) },
            new SetWrite { Path = "users/u1/notesArchive/z", Fields = Note("u1", 9) },   // a different id
            new SetWrite { Path = "users/u1/other/notes", Fields = Note("u1", 8) },      // a document named notes
            new SetWrite { Path = "users/u1/notes/a/notes/deep", Fields = Note("u1", 6) },
        ]);
    }

    private static Query Group(Filter? where = null, IReadOnlyList<Ordering>? orderBy = null, int? limit = null, Cursor? start = null) =>
        new("notes", where, orderBy, limit, start, CollectionGroup: true);

    private static FieldPath F(string p) => FieldPath.Parse(p);

    [Fact]
    public async Task Reads_every_collection_with_the_id_under_any_parent()
    {
        await SeedAsync();
        var all = (await Core().QueryAsync(Group())).Documents.Select(d => d.Path).ToList();
        Assert.Equal(["notes/top", "teams/t1/notes/d", "users/u1/notes/a", "users/u1/notes/a/notes/deep", "users/u1/notes/b", "users/u2/notes/c"], all);

        var mine = await Core().QueryAsync(Group(new FieldFilter(F("owner"), FilterOperator.Eq, new StringValue("u1")), [new Ordering(F("n"))]));
        Assert.Equal(["b", "a", "deep"], mine.Documents.Select(d => d.Id));

        var top2 = await Core().QueryAsync(Group(orderBy: [new Ordering(F("n"), SortDirection.Desc)], limit: 2));
        Assert.Equal(["deep", "d"], top2.Documents.Select(d => d.Id));

        var after = await Core().QueryAsync(Group(orderBy: [new Ordering(F("n"))], start: new Cursor([new IntegerValue(3)], Before: false)));
        Assert.Equal(["top", "d", "deep"], after.Documents.Select(d => d.Id));

        Assert.Equal(6, await Core().CountAsync(Group()));
        var sum = await Core().AggregateAsync(Group(), [Aggregation.Sum("n", "s")]);
        Assert.Equal(new IntegerValue(21), sum.Values["s"]);
    }

    [Fact]
    public async Task The_id_must_be_one_segment_and_the_flag_round_trips_as_json()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => Core().QueryAsync(new Query("users/u1/notes", CollectionGroup: true)));

        var q = Group(new FieldFilter(F("owner"), FilterOperator.Eq, new StringValue("u1")));
        var json = QueryAstWriter.Write(q);
        Assert.True(json["collectionGroup"]!.GetValue<bool>());
        Assert.Equal(q, QueryParser.Parse(json));
        Assert.NotEqual(QueryKey.Compute(q), QueryKey.Compute(q with { CollectionGroup = false }));
        Assert.Null(QueryAstWriter.Write(q with { CollectionGroup = false })["collectionGroup"]);
    }

    [Fact]
    public async Task A_collection_group_index_serves_the_group_query()
    {
        await SeedAsync();
        var index = new IndexDefinition("notes", [new("owner"), new("n")], CollectionGroup: true);
        var schema = new SchemaManager(Fx.DataSource, [index]);
        await schema.SyncIndexesAsync();
        try
        {
            var query = Group(new FieldFilter(F("owner"), FilterOperator.Eq, new StringValue("u1")), [new Ordering(F("n"))]);
            var scopes = new CollectionIndexResolver([index]);
            Assert.Null(scopes.Advise(query, Normalizer.Normalize(query)));
            Assert.Equal("""ib.AddGroup("notes", [new("owner"), new("n")])""", new CollectionIndexResolver([]).Advise(query, Normalizer.Normalize(query)));
            Assert.Equal(["b", "a", "deep"], (await Core(scopes: scopes).QueryAsync(query)).Documents.Select(d => d.Id));

            var compiled = SqlCompiler.Compile(Normalizer.Normalize(query), scopes.ScopeFor(query.Collection), null);
            await using var conn = await Fx.DataSource.OpenConnectionAsync();
            await using (var off = conn.CreateCommand()) { off.CommandText = "SET enable_seqscan = off"; await off.ExecuteNonQueryAsync(); }
            await using var explain = conn.CreateCommand();
            explain.CommandText = "EXPLAIN " + compiled.Sql;
            explain.Parameters.AddRange(compiled.Parameters);
            var plan = new List<string>();
            await using (var r = await explain.ExecuteReaderAsync()) while (await r.ReadAsync()) plan.Add(r.GetString(0));
            Assert.Contains(plan, l => l.Contains(IndexSql.NameOf(index)));
        }
        finally
        {
            await new SchemaManager(Fx.DataSource, []).SyncIndexesAsync();
        }
    }

    // ── rules ─────────────────────────────────────────────────────────────

    private RuleGuardedDocumentDatabase Guard(string rules, string uid) =>
        new(Core(), new RuleEngine(new StaticRuleSetRepository(RuleText.ParseRuleSet(rules)), WincheRuleValueComparer.Instance),
            new StaticClaimsAccessor(new Dictionary<string, object?> { ["uid"] = uid }));

    [Fact]
    public async Task Rules_must_cover_every_parent()
    {
        await SeedAsync();
        const string anyParent = "match {path=**}/notes/{noteId} {\n  allow list: if resource.owner == request.auth.uid;\n}";
        var mine = Group(new FieldFilter(F("owner"), FilterOperator.Eq, new StringValue("u1")));

        Assert.Equal(3, (await Guard(anyParent, "u1").QueryAsync(mine)).Documents.Count);
        await Assert.ThrowsAsync<AccessDeniedException>(() => Guard(anyParent, "u1").QueryAsync(Group()));   // not constrained to me
        await Assert.ThrowsAsync<AccessDeniedException>(() => Guard(anyParent, "u2").QueryAsync(mine));      // someone else's

        const string oneParent = "match users/{uid}/notes/{noteId} {\n  allow list: if request.auth.uid == uid;\n}";
        await Assert.ThrowsAsync<AccessDeniedException>(() => Guard(oneParent, "u1").QueryAsync(mine));      // doesn't cover teams/…

        // The same rule still governs an ordinary query on one of those collections.
        Assert.Equal(2, (await Guard(anyParent, "u1").QueryAsync(new Query("users/u1/notes",
            new FieldFilter(F("owner"), FilterOperator.Eq, new StringValue("u1"))))).Documents.Count);
    }

    // ── live ──────────────────────────────────────────────────────────────

    private static async Task<T> NextAsync<T>(IAsyncEnumerator<T> e, int timeoutMs = 10000)
    {
        var move = e.MoveNextAsync().AsTask();
        Assert.True(await Task.WhenAny(move, Task.Delay(timeoutMs)) == move, "timed out waiting for a snapshot");
        Assert.True(await move);
        return e.Current;
    }

    [Fact]
    public async Task A_live_group_query_sees_changes_under_any_parent_and_resumes()
    {
        await SeedAsync();
        var registry = new QueryListenerRegistry(Fx.DataSource);
        var db = Core(registry);
        var pump = new ChangeFeedPump(Fx.DataSource, [registry],
            new ChangeFeedConfig { PollInterval = TimeSpan.FromMilliseconds(100) }, NullLogger<ChangeFeedPump>.Instance);
        using var cts = new CancellationTokenSource();
        var run = Task.Run(() => pump.RunAsync(cts.Token));
        try
        {
            await Task.Delay(300);
            var query = Group(orderBy: [new Ordering(F("n"))]);
            long token;
            await using (var listener = db.Listen(query))
            await using (var e = listener.Snapshots().GetAsyncEnumerator())
            {
                Assert.Equal(6, (await NextAsync(e)).Documents.Count);

                await db.WriteAsync([new SetWrite { Path = "users/u1/notesArchive/y", Fields = Note("u1", 0) }]);   // not in the group
                await db.WriteAsync([new SetWrite { Path = "projects/p9/notes/new", Fields = Note("p9", 0) }]);     // a new parent
                var snap = await NextAsync(e);
                Assert.Equal("new", snap.Documents[0].Id);
                Assert.Equal(7, snap.Documents.Count);
                token = snap.ResumeToken;
            }
            Assert.Equal(1, registry.QueriesRun);   // applied in memory

            await using (var resumed = db.Listen(query, new ListenOptions(ResumeFrom: token)))
            await using (var er = resumed.Snapshots().GetAsyncEnumerator())
                Assert.True((await NextAsync(er)).Current);

            await db.WriteAsync([new SetWrite { Path = "users/u2/notes/c", Fields = Note("u2", 7) }]);
            await Task.Delay(500);
            await using (var stale = db.Listen(query, new ListenOptions(ResumeFrom: token)))
            await using (var es = stale.Snapshots().GetAsyncEnumerator())
                Assert.False((await NextAsync(es)).Current);
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }
}
