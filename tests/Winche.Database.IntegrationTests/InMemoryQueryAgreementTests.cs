using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Winche.Database.DependencyInjection;
using Winche.Database.Documents;
using Winche.Database.Querying;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Matching;
using Winche.Database.Runtime;
using Winche.Database.Runtime.ChangeFeed;
using Winche.Database.Runtime.Listening;
using Winche.Database.Runtime.Writes;
using Winche.Values;

namespace Winche.Database.IntegrationTests;

/// <summary>
/// Live queries apply changes in memory when they can (10.5.0). The in-memory result must always equal
/// what the query returns from Postgres: random writes over mixed value types, many query shapes, each
/// round's real feed batch applied and compared with a fresh query.
/// </summary>
[Collection("postgres")]
public class InMemoryQueryAgreementTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private const string Coll = "agree";

    private static readonly string[] Strings = ["", "a", "ab", "b", "B", "Z", "é", "e", "zz", "\U0001F600", "�", "a b"];

    private static Value RandomValue(Random rng) => rng.Next(9) switch
    {
        0 => new IntegerValue(rng.Next(-5, 6)),
        1 => new DoubleValue(Math.Round(rng.NextDouble() * 11 - 5.5, 1)),
        2 => new DoubleValue(rng.Next(4) == 0 ? double.NaN : rng.Next(-3, 4)),   // NaN, or an integral double
        3 => new StringValue(Strings[rng.Next(Strings.Length)]),
        4 => new NullValue(),
        5 => new BooleanValue(rng.Next(2) == 0),
        6 => new TimestampValue(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(rng.Next(-3, 4))),
        7 => new IntegerValue(rng.Next(-2, 3)),
        _ => new IntegerValue(rng.Next(0, 3)),
    };

    private static Dictionary<string, Value> RandomFields(Random rng)
    {
        var fields = new Dictionary<string, Value>();
        if (rng.Next(6) > 0) fields["n"] = RandomValue(rng);
        if (rng.Next(6) > 0) fields["s"] = rng.Next(4) == 0 ? RandomValue(rng) : new StringValue(Strings[rng.Next(Strings.Length)]);
        if (rng.Next(3) > 0) fields["tag"] = new StringValue(rng.Next(3) switch { 0 => "a", 1 => "b", _ => "c" });
        if (rng.Next(3) > 0) fields["m"] = new MapValue(new Dictionary<string, Value> { ["x"] = RandomValue(rng) });
        return fields;
    }

    private static FieldPath F(string p) => FieldPath.Parse(p);

    private static Cursor At(bool before, params Value[] values) => new(values, before);

    private static readonly Query[] Queries =
    [
        new(Coll),                                                                    // default limit 100: more than that exist
        new(Coll, OrderBy: [new Ordering(F("n"))]),
        new(Coll, OrderBy: [new Ordering(F("n"), SortDirection.Desc)], Limit: 500),  // effectively everything
        new(Coll, Where: new FieldFilter(F("n"), FilterOperator.Gt, new IntegerValue(0)), OrderBy: [new Ordering(F("n"))]),
        new(Coll, Where: new FieldFilter(F("s"), FilterOperator.Gte, new StringValue("b")), OrderBy: [new Ordering(F("s"), SortDirection.Desc)]),
        new(Coll, Where: new FieldFilter(F("tag"), FilterOperator.Eq, new StringValue("a")), OrderBy: [new Ordering(F("m.x"))]),
        new(Coll, OrderBy: [new Ordering(F("n"))], Limit: 4),
        new(Coll, Where: new FieldFilter(F("tag"), FilterOperator.In, new ArrayValue([new StringValue("a"), new StringValue("b")])),
            OrderBy: [new Ordering(F("s"), SortDirection.Desc)], Limit: 3),
        new(Coll, Where: new CompositeFilter(CompositeOp.Or, [
            new FieldFilter(F("n"), FilterOperator.Eq, new IntegerValue(1)),
            new FieldFilter(F("s"), FilterOperator.Eq, new StringValue("a"))])),
        new(Coll, OrderBy: [new Ordering(F("tag")), new Ordering(F("n"), SortDirection.Desc)], Limit: 7),
        new(Coll, Where: new FieldFilter(F("n"), FilterOperator.Ne, new NullValue()), OrderBy: [new Ordering(F("n"), SortDirection.Desc)], Limit: 6),
        // select (whole documents held, projection applied on output; values include null)
        new(Coll, OrderBy: [new Ordering(F("n"))], Limit: 5, Select: [F("n"), F("m.x")]),
        new(Coll, Where: new FieldFilter(F("tag"), FilterOperator.Eq, new StringValue("b")), Select: [F("m"), F("s")]),
        // cursors
        new(Coll, OrderBy: [new Ordering(F("n"))], Start: At(true, new IntegerValue(0)), Limit: 8),
        new(Coll, OrderBy: [new Ordering(F("s"), SortDirection.Desc)], Start: At(false, new StringValue("b")), End: At(false, new StringValue("")), Limit: 9),
        new(Coll, OrderBy: [new Ordering(F("n"))], Start: At(false, new IntegerValue(1), new StringValue($"{Coll}/d50")), Limit: 5),
        new(Coll, OrderBy: [new Ordering(F("tag"))], End: At(true, new StringValue("b"))),
        // limitToLast
        new(Coll, OrderBy: [new Ordering(F("n"))], LimitToLast: 4),
        new(Coll, Where: new FieldFilter(F("tag"), FilterOperator.In, new ArrayValue([new StringValue("a"), new StringValue("b")])),
            OrderBy: [new Ordering(F("s"), SortDirection.Desc)], LimitToLast: 3, Select: [F("s")]),
    ];

    private static bool SameFields(Document a, Document b) =>
        a.Fields.Count == b.Fields.Count
        && ValueComparer.Instance.Compare(new MapValue(a.Fields), new MapValue(b.Fields)) == 0;

    private async Task<IReadOnlyList<Document>> FreshAsync(Query query)
    {
        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        return (await new QueryExecutor(conn, null).ExecuteAsync(query)).Documents;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task The_in_memory_result_always_equals_the_query_result(int seed)
    {
        var rng = new Random(seed);
        var applier = new WriteApplier(Fx.DataSource);
        var reader = new ChangeFeedReader(Fx.DataSource);
        const int pool = 140;

        // Start with more documents than the default limit (100).
        for (var i = 0; i < 130; i += 10)
            await applier.ApplyAsync([.. Enumerable.Range(i, 10).Select(j => new SetWrite { Path = $"{Coll}/d{j}", Fields = RandomFields(rng) })]);
        var position = await reader.GetHeadAsync();

        var lives = Queries.Select(q => InMemoryQuery.For(q, buffer: 10)!).ToList();
        var held = new List<HeldWindow>();
        foreach (var live in lives) held.Add(live.FromFetch(await FreshAsync(live.FetchQuery)));
        int inMemory = 0, requeried = 0;

        for (var round = 0; round < 120; round++)
        {
            var writes = new List<Write>();
            var touched = new HashSet<string>();
            for (var w = rng.Next(1, 8); w > 0; w--)
            {
                var path = $"{Coll}/d{rng.Next(pool)}";
                if (!touched.Add(path)) continue;
                writes.Add(rng.Next(4) == 0 ? new DeleteWrite { Path = path } : new SetWrite { Path = path, Fields = RandomFields(rng) });
            }
            await applier.ApplyAsync(writes);

            var records = await reader.ReadAfterAsync(position, 500);
            if (records.Count == 0) continue;
            position = records[^1].Position;
            var batch = new ChangeBatch(records, await reader.FetchDocumentsAsync(records));

            for (var i = 0; i < Queries.Length; i++)
            {
                if (lives[i].TryApply(held[i], batch) is { } applied) { held[i] = applied; inMemory++; }
                else { held[i] = lives[i].FromFetch(await FreshAsync(lives[i].FetchQuery)); requeried++; }

                var fresh = await FreshAsync(Queries[i]);
                var result = lives[i].Result(held[i]);
                var where = $"seed {seed} round {round} query {i}";
                Assert.True(fresh.Select(d => d.Path).SequenceEqual(result.Select(d => d.Path)),
                    $"{where}:\n  sql:       {string.Join(", ", fresh.Select(d => d.Id))}\n  in memory: {string.Join(", ", result.Select(d => d.Id))}");
                for (var j = 0; j < fresh.Count; j++)
                {
                    Assert.True(fresh[j].Version == result[j].Version, $"{where}: version of {fresh[j].Id}");
                    Assert.True(SameFields(fresh[j], result[j]), $"{where}: fields of {fresh[j].Id}");
                }
            }
        }

        // Most updates must have been in memory, or the feature does nothing.
        Assert.True(inMemory > requeried * 4, $"in memory {inMemory}, re-queried {requeried}");
    }

    [Fact]
    public async Task Select_returns_fields_that_hold_null()
    {
        // The SQL projection used to strip {"nullValue": null} to an undecodable {}.
        await SeedDoc("p", new()
        {
            ["f"] = new NullValue(),
            ["a"] = new ArrayValue([new NullValue(), new IntegerValue(1)]),
            ["m"] = new MapValue(new Dictionary<string, Value> { ["x"] = new NullValue(), ["y"] = new IntegerValue(2) }),
            ["skip"] = new IntegerValue(3),
        }, "proj");

        var doc = Assert.Single((await Run(new Query("proj", Select: [F("f"), F("a"), F("m.x"), F("missing"), F("skip.deeper")]))).Documents);

        Assert.Equal(["a", "f", "m"], doc.Fields.Keys.Order(StringComparer.Ordinal));
        Assert.IsType<NullValue>(doc.Fields["f"]);
        Assert.Equal(2, ((ArrayValue)doc.Fields["a"]).Values.Count);
        var m = (MapValue)doc.Fields["m"];
        Assert.Equal(["x"], m.Fields.Keys);
        Assert.IsType<NullValue>(m.Fields["x"]);
    }

    [Fact]
    public void Only_an_offset_always_requeries()
    {
        Assert.Null(InMemoryQuery.For(new Query(Coll, OrderBy: [new Ordering(F("n"))], Offset: 2), 10));
        Assert.NotNull(InMemoryQuery.For(new Query(Coll, OrderBy: [new Ordering(F("n"))], Offset: 0), 10));
        Assert.NotNull(InMemoryQuery.For(new Query(Coll, Select: [F("n")]), 10));
        Assert.NotNull(InMemoryQuery.For(new Query(Coll, OrderBy: [new Ordering(F("n"))], LimitToLast: 2), 10));
        Assert.NotNull(InMemoryQuery.For(new Query(Coll, OrderBy: [new Ordering(F("n"))], Start: At(true, new IntegerValue(1))), 10));
    }

    [Fact]
    public async Task A_live_query_is_updated_without_running_it_again()
    {
        var registry = new QueryListenerRegistry(Fx.DataSource);
        var db = new DocumentDatabase(Fx.DataSource, Options.Create(new WincheDatabaseOptions()), registry);
        var pump = new ChangeFeedPump(Fx.DataSource, [registry],
            new ChangeFeedConfig { PollInterval = TimeSpan.FromMilliseconds(100) }, NullLogger<ChangeFeedPump>.Instance);
        using var cts = new CancellationTokenSource();
        var run = Task.Run(() => pump.RunAsync(cts.Token));
        try
        {
            await Task.Delay(300);
            await using var listener = db.Listen(new Query("live", OrderBy: [new Ordering(F("n"), SortDirection.Desc)]));
            await using var e = listener.Snapshots().GetAsyncEnumerator();
            Assert.True(await e.MoveNextAsync());   // initial snapshot (the one query)

            for (var i = 0; i < 5; i++)
            {
                await db.WriteAsync([new SetWrite { Path = $"live/d{i}", Fields = new Dictionary<string, Value> { ["n"] = new IntegerValue(i) } }]);
                var next = e.MoveNextAsync().AsTask();
                Assert.True(await Task.WhenAny(next, Task.Delay(10000)) == next && await next);
            }
            Assert.Equal(["d4", "d3", "d2", "d1", "d0"], e.Current.Documents.Select(d => d.Id));
            Assert.Equal(1, registry.QueriesRun);
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }
}
