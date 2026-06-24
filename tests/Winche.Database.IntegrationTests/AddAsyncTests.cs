using Microsoft.Extensions.Options;
using Winche.Database.DependencyInjection;
using Winche.Database.Documents;
using Winche.Database.Runtime;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class AddAsyncTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private DocumentDatabase Db() =>
        new(Fx.DataSource, Options.Create(new WincheDatabaseOptions { ConnectionString = Fx.ConnectionString }));

    private async Task<Document?> Get(string path)
    {
        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        return await new DocumentOperations(conn, null).GetAsync(path);
    }

    [Fact]
    public async Task AddAsync_GeneratesId_CreatesDocument()
    {
        var doc = await Db().AddAsync("people", new Dictionary<string, Value> { ["name"] = new StringValue("ada") });

        Assert.Equal(20, doc.Id.Length);
        Assert.Equal("people", doc.Collection);
        Assert.Equal($"people/{doc.Id}", doc.Path);
        Assert.Equal(1, doc.Version);
        Assert.Equal(new StringValue("ada"), doc.Fields["name"]);

        var stored = await Get(doc.Path);
        Assert.NotNull(stored);
        Assert.Equal(new StringValue("ada"), stored!.Fields["name"]);
        Assert.Equal(doc.UpdateTime, stored.UpdateTime);
    }

    [Fact]
    public async Task AddAsync_TwoCalls_ProduceDistinctIds()
    {
        var db = Db();
        var a = await db.AddAsync("people", new Dictionary<string, Value> { ["n"] = new IntegerValue(1) });
        var b = await db.AddAsync("people", new Dictionary<string, Value> { ["n"] = new IntegerValue(2) });
        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public async Task AddAsync_InvalidCollectionPath_Throws()
    {
        // "people/ada" is a document path (even segment count), not a collection path.
        var ex = await Assert.ThrowsAsync<RuntimeException>(
            () => Db().AddAsync("people/ada", new Dictionary<string, Value> { ["n"] = new IntegerValue(1) }));
        Assert.Equal(RuntimeStatus.InvalidArgument, ex.Status);
    }
}
