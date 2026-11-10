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
using Winche.Database.Values;

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

    private static readonly Query[] Queries =
    [
        new(Coll),
        new(Coll, OrderBy: [new Ordering(F("n"))]),
        new(Coll, OrderBy: [new Ordering(F("n"), SortDirection.Desc)]),
        new(Coll, Where: new FieldFilter(F("n"), FilterOperator.Gt, new IntegerValue(0)), OrderBy: [new Ordering(F("n"))]),
        new(Coll, Where: new FieldFilter(F("s"), FilterOperator.Gte, new StringValue("b")), OrderBy: [new Ordering(F("s"), SortDirection.Desc)]),
        new(Coll, Where: new FieldFilter(F("tag"), FilterOperator.Eq, new StringValue("a")), OrderBy: [new Ordering(F("m.x"))]),
        new(Coll, OrderBy: [new Ordering(F("n"))], Limit: 4),
        new(Coll, Where: new FieldFilter(F("tag"), FilterOperator.In, new ArrayValue([new StringValue("a"), new StringValue("b")])),
            OrderBy: [new Ordering(F("s"), SortDirection.Desc)], Limit: 3),
        new(Coll, Where: new CompositeFilter(CompositeOp.Or, [
            new FieldFilter(F("n"), FilterOperator.Eq, new IntegerValue(1)),
            new FieldFilter(F("s"), FilterOperator.Eq, new StringValue("a"))])),
        new(Coll, OrderBy: [new Ordering(F("tag")), new Ordering(F("n"), SortDirection.Desc)]),
        new(Coll, Where: new FieldFilter(F("n"), FilterOperator.Ne, new NullValue()), OrderBy: [new Ordering(F("n"), SortDirection.Desc)], Limit: 6),
    ];

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
        var position = await reader.GetHeadAsync();

        var states = new List<IReadOnlyList<Document>>();
        foreach (var q in Queries) states.Add(await FreshAsync(q));
        var keys = Queries.Select(InMemoryQuery.SortKeysFor).ToList();
        var predicates = Queries.Select(q => { ChangeMatcher.TryPreparePredicate(q, out var p); return p; }).ToList();
        int inMemory = 0, requeried = 0;

        for (var round = 0; round < 120; round++)
        {
            var writes = new List<Write>();
            var touched = new HashSet<string>();
            for (var w = rng.Next(1, 6); w > 0; w--)
            {
                var path = $"{Coll}/d{rng.Next(25)}";
                if (!touched.Add(path)) continue;
                writes.Add(rng.Next(5) == 0 ? new DeleteWrite { Path = path } : new SetWrite { Path = path, Fields = RandomFields(rng) });
            }
            await applier.ApplyAsync(writes);

            var records = await reader.ReadAfterAsync(position, 500);
            if (records.Count == 0) continue;
            position = records[^1].Position;
            var batch = new ChangeBatch(records, await reader.FetchDocumentsAsync(records));

            for (var i = 0; i < Queries.Length; i++)
            {
                var fresh = await FreshAsync(Queries[i]);
                var applied = keys[i] is { } k ? InMemoryQuery.TryApply(Queries[i], predicates[i], k, states[i], batch) : null;
                if (applied is null) { requeried++; states[i] = fresh; continue; }
                inMemory++;
                Assert.True(fresh.Select(d => d.Path).SequenceEqual(applied.Select(d => d.Path)),
                    $"seed {seed} round {round} query {i}:\n  sql:       {string.Join(", ", fresh.Select(d => d.Id))}\n  in memory: {string.Join(", ", applied.Select(d => d.Id))}");
                Assert.True(fresh.Select(d => d.Version).SequenceEqual(applied.Select(d => d.Version)), $"seed {seed} round {round} query {i}: versions differ");
                states[i] = applied;
            }
        }

        // Most updates must have been in memory, or the feature does nothing.
        Assert.True(inMemory > requeried * 3, $"in memory {inMemory}, re-queried {requeried}");
    }

    [Fact]
    public void Queries_that_depend_on_documents_outside_the_result_always_requery()
    {
        Assert.Null(InMemoryQuery.SortKeysFor(new Query(Coll, Select: [F("n")])));
        Assert.Null(InMemoryQuery.SortKeysFor(new Query(Coll, OrderBy: [new Ordering(F("n"))], Offset: 2)));
        Assert.Null(InMemoryQuery.SortKeysFor(new Query(Coll, OrderBy: [new Ordering(F("n"))], LimitToLast: 2)));
        Assert.Null(InMemoryQuery.SortKeysFor(new Query(Coll, OrderBy: [new Ordering(F("n"))],
            Start: new Cursor([new IntegerValue(1)], Before: true))));
        Assert.NotNull(InMemoryQuery.SortKeysFor(new Query(Coll, OrderBy: [new Ordering(F("n"))], Limit: 3)));
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
