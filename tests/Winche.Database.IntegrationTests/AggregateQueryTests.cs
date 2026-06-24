using Winche.Database.Documents;
using Winche.Database.Querying;
using Winche.Database.Querying.Ast;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class AggregateQueryTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private static Dictionary<string, Value> Map(params (string K, Value V)[] e) => e.ToDictionary(x => x.K, x => x.V);

    private async Task Seed(string id, Dictionary<string, Value> fields)
    {
        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        await new DocumentOperations(conn, null).SetAsync($"c/{id}", fields);
    }

    private async Task<AggregationResult> Aggregate(Query query, params Aggregation[] aggs)
    {
        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        return await new QueryExecutor(conn, null).AggregateAsync(query, aggs);
    }

    [Fact]
    public async Task Sum_Integers_ReturnsIntegerValue()
    {
        await Seed("a", Map(("n", new IntegerValue(2))));
        await Seed("b", Map(("n", new IntegerValue(3))));
        await Seed("c", Map(("x", new StringValue("ignored"))));   // non-numeric ignored
        await Seed("d", Map());                                     // missing field ignored

        var r = await Aggregate(new Query("c"), Aggregation.Sum("n", "total"));
        Assert.Equal(new IntegerValue(5), r.Values["total"]);
    }

    [Fact]
    public async Task Sum_WithDouble_ReturnsDoubleValue()
    {
        await Seed("a", Map(("n", new IntegerValue(2))));
        await Seed("b", Map(("n", new DoubleValue(1.5))));

        var r = await Aggregate(new Query("c"), Aggregation.Sum("n", "total"));
        Assert.Equal(new DoubleValue(3.5), r.Values["total"]);
    }

    [Fact]
    public async Task Average_ReturnsDouble_EmptyReturnsNull()
    {
        await Seed("a", Map(("n", new IntegerValue(2))));
        await Seed("b", Map(("n", new IntegerValue(4))));

        var r = await Aggregate(new Query("c"), Aggregation.Average("n", "avg"));
        Assert.Equal(new DoubleValue(3.0), r.Values["avg"]);

        var empty = await Aggregate(new Query("nope"), Aggregation.Average("n", "avg"));
        Assert.Equal(new NullValue(), empty.Values["avg"]);
    }

    [Fact]
    public async Task EmptySum_ReturnsZero_AndCountZero()
    {
        var r = await Aggregate(new Query("nope"), Aggregation.Sum("n", "s"), Aggregation.Count("c"));
        Assert.Equal(new IntegerValue(0), r.Values["s"]);
        Assert.Equal(new IntegerValue(0), r.Values["c"]);
    }

    [Fact]
    public async Task Combined_CountSumAverage_InOneCall()
    {
        await Seed("a", Map(("n", new IntegerValue(2))));
        await Seed("b", Map(("n", new IntegerValue(4))));

        var r = await Aggregate(new Query("c"),
            Aggregation.Count("cnt"), Aggregation.Sum("n", "sum"), Aggregation.Average("n", "avg"));
        Assert.Equal(new IntegerValue(2), r.Values["cnt"]);
        Assert.Equal(new IntegerValue(6), r.Values["sum"]);
        Assert.Equal(new DoubleValue(3.0), r.Values["avg"]);
    }

    [Fact]
    public async Task Filtered_AggregatesOnlyMatching()
    {
        await Seed("a", Map(("n", new IntegerValue(2)), ("hot", new BooleanValue(true))));
        await Seed("b", Map(("n", new IntegerValue(3)), ("hot", new BooleanValue(false))));

        var q = new Query("c", Where: new FieldFilter(F("hot"), FilterOperator.Eq, new BooleanValue(true)));
        var r = await Aggregate(q, Aggregation.Sum("n", "s"));
        Assert.Equal(new IntegerValue(2), r.Values["s"]);
    }

    [Fact]
    public async Task Sum_WithNaN_PropagatesNaN()
    {
        await Seed("a", Map(("n", new IntegerValue(2))));
        await Seed("b", Map(("n", new DoubleValue(double.NaN))));

        var r = await Aggregate(new Query("c"), Aggregation.Sum("n", "s"));
        var d = Assert.IsType<DoubleValue>(r.Values["s"]);
        Assert.True(double.IsNaN(d.Value));
    }

    [Fact]
    public async Task Sum_IntegerOverflow_ReturnsDouble()
    {
        await Seed("a", Map(("n", new IntegerValue(long.MaxValue))));
        await Seed("b", Map(("n", new IntegerValue(long.MaxValue))));

        // exact integer sum (2 * long.MaxValue) exceeds Int64 range → promotes to double
        var r = await Aggregate(new Query("c"), Aggregation.Sum("n", "s"));
        Assert.IsType<DoubleValue>(r.Values["s"]);
    }

    [Fact]
    public async Task LimitCapped_AggregatesOnlyLimitedRows()
    {
        for (var i = 0; i < 5; i++)
            await Seed($"d{i}", Map(("n", new IntegerValue(1))));

        // explicit Limit caps the aggregate (like count): only 3 of the 5 one-valued rows are summed/counted.
        var q = new Query("c", Limit: 3);
        var r = await Aggregate(q, Aggregation.Count("cnt"), Aggregation.Sum("n", "s"));
        Assert.Equal(new IntegerValue(3), r.Values["cnt"]);
        Assert.Equal(new IntegerValue(3), r.Values["s"]);
    }
}
