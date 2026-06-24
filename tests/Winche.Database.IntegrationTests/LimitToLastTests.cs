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
public class LimitToLastTests(PostgresFixture fx) : QueryTestBase(fx)
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
    public async Task LimitToLast_ReturnsTail_InAscendingOrder()
    {
        await SeedN(5); // i = 0..4
        var r = await Db().QueryAsync(new Query("c", OrderBy: [new Ordering(F("i"))], LimitToLast: 2));
        Assert.Equal(new long[] { 3, 4 }, Values(r));
    }

    [Fact]
    public async Task LimitToLast_DescOrder_ReturnsTail()
    {
        await SeedN(5);
        var r = await Db().QueryAsync(
            new Query("c", OrderBy: [new Ordering(F("i"), SortDirection.Desc)], LimitToLast: 2));
        // desc order is 4,3,2,1,0 → last two are 1,0
        Assert.Equal(new long[] { 1, 0 }, Values(r));
    }

    [Fact]
    public async Task LimitToLast_WithoutOrderBy_Throws()
    {
        var ex = await Assert.ThrowsAsync<PlanValidationException>(
            () => Db().QueryAsync(new Query("c", LimitToLast: 2)));
        Assert.Equal("LIMIT_TO_LAST_NO_ORDER", ex.Code);
    }

    [Fact]
    public async Task LimitAndLimitToLast_Throws()
    {
        var ex = await Assert.ThrowsAsync<PlanValidationException>(
            () => Db().QueryAsync(new Query("c", OrderBy: [new Ordering(F("i"))], Limit: 1, LimitToLast: 2)));
        Assert.Equal("LIMIT_CONFLICT", ex.Code);
    }

    [Fact]
    public async Task OffsetAndLimitToLast_Throws()
    {
        var ex = await Assert.ThrowsAsync<PlanValidationException>(
            () => Db().QueryAsync(new Query("c", OrderBy: [new Ordering(F("i"))], Offset: 1, LimitToLast: 2)));
        Assert.Equal("OFFSET_LIMIT_TO_LAST", ex.Code);
    }

    [Fact]
    public async Task LimitToLast_Zero_Throws()
    {
        var ex = await Assert.ThrowsAsync<PlanValidationException>(
            () => Db().QueryAsync(new Query("c", OrderBy: [new Ordering(F("i"))], LimitToLast: 0)));
        Assert.Equal("BAD_LIMIT_TO_LAST", ex.Code);
    }
}
