using Microsoft.Extensions.Options;
using Winche.Database.Documents;
using Winche.Database.DependencyInjection;
using Winche.Database.Querying.Ast;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class DocumentDatabaseTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private DocumentDatabase Db() => new(Fx.DataSource,
        Options.Create(new WincheDatabaseOptions()));

    private static Dictionary<string, Value> Map(params (string K, Value V)[] e) => e.ToDictionary(x => x.K, x => x.V);

    [Fact]
    public async Task Facade_CrudQueryBatch()
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

        var q = await db.QueryAsync(new Query("users",
            Where: new FieldFilter(F("age"), FilterOperator.Gte, new IntegerValue(40))));
        Assert.Equal(2, q.Documents.Count);

        await db.WriteAsync([new DeleteWrite { Path = "users/u1" }]);
        Assert.Null(await db.GetAsync("users/u1"));
    }

    [Fact]
    public void Listen_WithoutRegistry_Throws()
    {
        Assert.Throws<NotSupportedException>(() => Db().Listen(new Query("c")));
    }

    [Fact]
    public async Task GetAll_MalformedPath_Throws()
    {
        var db = Db();
        // "not-a-doc-path" has zero slashes → odd-segment count fails document-path check
        await Assert.ThrowsAsync<ArgumentException>(() => db.GetAllAsync(["c/a", "not-a-doc-path"]));
    }

    [Fact]
    public async Task GetAll_LargeBatch_SingleQuery_OrderAndNulls()
    {
        var db = Db();
        var writes = Enumerable.Range(0, 25)
            .Select(i => (Write)new SetWrite { Path = $"gma/d{i:D2}", Fields = Map(("n", new IntegerValue(i))) })
            .ToList();
        await db.WriteAsync(writes);

        var paths = new List<string> { "gma/d10", "gma/none", "gma/d03", "gma/d24", "gma/none2", "gma/d00" };
        var docs = await db.GetAllAsync(paths);
        Assert.Equal(6, docs.Count);
        Assert.Equal(new IntegerValue(10), docs[0]!.Fields["n"]);
        Assert.Null(docs[1]);
        Assert.Equal(new IntegerValue(3), docs[2]!.Fields["n"]);
        Assert.Equal(new IntegerValue(24), docs[3]!.Fields["n"]);
        Assert.Null(docs[4]);
        Assert.Equal(new IntegerValue(0), docs[5]!.Fields["n"]);
    }
}
