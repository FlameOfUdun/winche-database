using Microsoft.Extensions.Options;
using Winche.Database.DependencyInjection;
using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Runtime;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class SnapshotCursorTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private DocumentDatabase Db() =>
        new(Fx.DataSource, Options.Create(new WincheDatabaseOptions { ConnectionString = Fx.ConnectionString }));

    private async Task SeedN(int n)
    {
        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        var ops = new DocumentOperations(conn, null);
        for (var i = 0; i < n; i++)
            await ops.SetAsync($"c/{i:D2}", new Dictionary<string, Value> { ["i"] = new IntegerValue(i) });
    }

    [Fact]
    public async Task StartAfter_Snapshot_ReturnsFollowingDocuments()
    {
        await SeedN(5); // i = 0..4 (ids c/00..c/04)
        var db = Db();
        var mid = await db.GetAsync("c/02");
        Assert.NotNull(mid);

        var orderBy = new[] { new Ordering(F("i")) };
        var cursor = Cursor.FromDocument(mid!, orderBy, before: false); // startAfter: exclusive of mid
        var r = await db.QueryAsync(new Query("c", OrderBy: orderBy, Start: cursor));

        var values = r.Documents.Select(d => ((IntegerValue)d.Fields["i"]).Value).ToList();
        Assert.Equal(new long[] { 3, 4 }, values);
    }
}
