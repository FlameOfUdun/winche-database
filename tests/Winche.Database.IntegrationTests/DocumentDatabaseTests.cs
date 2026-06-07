using Microsoft.Extensions.Options;
using Winche.Database.Documents;
using Winche.Database.Models;
using Winche.Database.Querying.Ast;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class DocumentDatabaseTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private DocumentDatabase Db() => new(Fx.DataSource,
        Options.Create(new StoreOptions { TableName = Fx.Table }));

    private static Dictionary<string, Value> Map(params (string K, Value V)[] e) => e.ToDictionary(x => x.K, x => x.V);

    [Fact]
    public async Task Facade_CrudQueryAggregateBatch()
    {
        var db = Db();

        await db.WriteAsync([new SetWrite { Path = "users/u1", Fields = Map(("age", new IntegerValue(30))) }]);
        await new WriteBatch(db)
            .Set("users/u2", Map(("age", new IntegerValue(40))))
            .Set("users/u3", Map(("age", new IntegerValue(50))))
            .CommitAsync();

        Assert.Equal(new IntegerValue(30), (await db.GetAsync("users/u1"))!.Fields["age"]);

        var all = await db.GetAllAsync(["users/u3", "users/none", "users/u1"]);
        Assert.Equal(3, all.Count);
        Assert.Equal(new IntegerValue(50), all[0]!.Fields["age"]);   // input order preserved
        Assert.Null(all[1]);
        Assert.Equal(new IntegerValue(30), all[2]!.Fields["age"]);

        var q = await db.QueryAsync(new QueryAst("users",
            Where: new FieldFilterAst(F("age"), FilterOperator.Gte, new IntegerValue(40))));
        Assert.Equal(2, q.Documents.Count);

        var agg = await db.AggregateAsync(new PipelineAst([
            new MatchStageAst("users", null),
            new GroupStageAst([], [new AccumulatorAst("n", AggFunction.Count)])]));
        Assert.Equal(new IntegerValue(3), Assert.Single(agg.Rows)["n"]);

        await db.WriteAsync([new DeleteWrite { Path = "users/u1" }]);
        Assert.Null(await db.GetAsync("users/u1"));
    }

    [Fact]
    public void Listen_WithoutRegistry_Throws()
    {
        Assert.Throws<NotSupportedException>(() => Db().Listen(new QueryAst("c")));
    }
}
