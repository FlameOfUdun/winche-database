using Microsoft.Extensions.Options;
using Winche.Database.DependencyInjection;
using Winche.Database.Documents;
using Winche.Database.Querying;
using Winche.Database.Querying.Ast;
using Winche.Database.Runtime;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class AggregateApiTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private DocumentDatabase Db() =>
        new(Fx.DataSource, Options.Create(new WincheDatabaseOptions { ConnectionString = Fx.ConnectionString }));

    private static Dictionary<string, Value> Map(params (string K, Value V)[] e) => e.ToDictionary(x => x.K, x => x.V);

    private async Task Seed(string id, Dictionary<string, Value> fields)
    {
        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        await new DocumentOperations(conn, null).SetAsync($"c/{id}", fields);
    }

    [Fact]
    public async Task AggregateAsync_CountSum_OverInterface()
    {
        await Seed("a", Map(("n", new IntegerValue(2))));
        await Seed("b", Map(("n", new IntegerValue(5))));

        var r = await Db().AggregateAsync(new Query("c"),
            [Aggregation.Count("cnt"), Aggregation.Sum("n", "sum"), Aggregation.Average("n", "avg")]);
        Assert.Equal(new IntegerValue(2), r.Values["cnt"]);
        Assert.Equal(new IntegerValue(7), r.Values["sum"]);
        Assert.Equal(new DoubleValue(3.5), r.Values["avg"]);
    }
}
