using Microsoft.Extensions.Options;
using Winche.Database.DependencyInjection;
using Winche.Database.Documents;
using Winche.Database.Querying;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Planning;
using Winche.Database.Runtime;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class OffsetQueryTests(PostgresFixture fx) : QueryTestBase(fx)
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

    private static long[] Values(QueryResult r) =>
        [.. r.Documents.Select(d => ((IntegerValue)d.Fields["i"]).Value)];

    [Fact]
    public async Task Offset_SkipsLeadingResults()
    {
        await SeedN(5);
        var r = await Db().QueryAsync(new Query("c", OrderBy: [new Ordering(F("i"))], Offset: 2));
        Assert.Equal(new long[] { 2, 3, 4 }, Values(r));
    }

    [Fact]
    public async Task Offset_ComposesWithLimit()
    {
        await SeedN(5);
        var r = await Db().QueryAsync(new Query("c", OrderBy: [new Ordering(F("i"))], Limit: 2, Offset: 1));
        Assert.Equal(new long[] { 1, 2 }, Values(r));
        Assert.True(r.HasMore); // rows 3,4 remain after the offset+limit window
    }

    [Fact]
    public async Task Offset_BeyondEnd_ReturnsEmpty()
    {
        await SeedN(5);
        var r = await Db().QueryAsync(new Query("c", OrderBy: [new Ordering(F("i"))], Offset: 10));
        Assert.Empty(r.Documents);
        Assert.False(r.HasMore);
    }

    [Fact]
    public async Task NegativeOffset_Throws()
    {
        var ex = await Assert.ThrowsAsync<PlanValidationException>(
            () => Db().QueryAsync(new Query("c", OrderBy: [new Ordering(F("i"))], Offset: -1)));
        Assert.Equal("BAD_OFFSET", ex.Code);
    }
}
