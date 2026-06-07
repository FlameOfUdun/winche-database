// tests/Winche.Database.IntegrationTests/ComparerSqlConsistencyTests.cs
using Winche.Database.Documents;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class ComparerSqlConsistencyTests(PostgresFixture fx) : QueryTestBase(fx)
{
    /// <summary>A corpus covering every type-class, edge values, nesting.</summary>
    private static List<Value> Corpus() =>
    [
        new NullValue(), new BooleanValue(false), new BooleanValue(true),
        new DoubleValue(double.NaN), new DoubleValue(double.NegativeInfinity),
        new IntegerValue(long.MinValue), new IntegerValue(-1), new DoubleValue(-0.5),
        new DoubleValue(-0.0), new IntegerValue(0), new DoubleValue(1e-300), new DoubleValue(0.5), new IntegerValue(7), new DoubleValue(7.0),
        new DoubleValue(double.PositiveInfinity),
        new TimestampValue(new DateTimeOffset(1969, 1, 1, 0, 0, 0, TimeSpan.Zero)),
        new TimestampValue(new DateTimeOffset(2026, 6, 6, 0, 0, 0, TimeSpan.Zero)),
        new StringValue(""), new StringValue("B"), new StringValue("a"), new StringValue("aa"), new StringValue("á"),
        new BytesValue([]), new BytesValue([0]), new BytesValue([1, 2]),
        new ReferenceValue("a/b"), new ReferenceValue("a/b/c/d"),
        new GeoPointValue(-10, 50), new GeoPointValue(10, -50), new GeoPointValue(10, 50),
        new ArrayValue([]), new ArrayValue([new IntegerValue(1)]),
        new ArrayValue([new IntegerValue(1), new StringValue("x")]), new ArrayValue([new IntegerValue(2)]),
        new MapValue(new Dictionary<string, Value>()),
        new MapValue(new Dictionary<string, Value> { ["a"] = new IntegerValue(1) }),
        new MapValue(new Dictionary<string, Value> { ["a"] = new IntegerValue(2) }),
        new MapValue(new Dictionary<string, Value> { ["b"] = new IntegerValue(1) }),
    ];

    [Fact]
    public async Task CSharpComparer_AgreesWithSqlOrdering()
    {
        var corpus = Corpus();
        for (var i = 0; i < corpus.Count; i++)
            await SeedDoc($"v{i:D3}", new Dictionary<string, Value> { ["f"] = corpus[i] });

        // SQL order via the full query path (six-expression family + winche_key)
        var sqlOrder = (await Run(new Winche.Database.Querying.Ast.QueryAst("c",
            OrderBy: [new Winche.Database.Querying.Ast.OrderAst(F("f"))], Limit: 1000)))
            .Documents.Select(d => d.Fields["f"]).ToList();

        // C# order over the same values (stable by original index for ties)
        var csOrder = corpus
            .Select((v, i) => (v, i))
            .OrderBy(t => t.v, ValueComparer.Instance)
            .ThenBy(t => $"c/v{t.i:D3}", StringComparer.Ordinal)   // mirror __name__ tiebreak
            .Select(t => t.v)
            .ToList();

        Assert.Equal(csOrder.Count, sqlOrder.Count);
        for (var i = 0; i < csOrder.Count; i++)
            Assert.True(ValueComparer.Instance.Compare(csOrder[i], sqlOrder[i]) == 0,
                $"Mismatch at {i}: C#={csOrder[i]} SQL={sqlOrder[i]}");
    }
}
