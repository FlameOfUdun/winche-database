using Microsoft.Extensions.Options;
using Winche.Database.DependencyInjection;
using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Planning;
using Winche.Database.Runtime;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

/// <summary>
/// The configured <see cref="QueryLimits"/> are enforced inside the query pipeline, not only at the
/// REST/WebSocket boundary — a caller reaching <see cref="IDocumentDatabase"/> directly is bounded
/// by the same ceilings, so a transport added later cannot silently omit them.
/// </summary>
[Collection("postgres")]
public class QueryLimitsCorePathTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private IDocumentDatabase Core(QueryLimits limits) =>
        new DocumentDatabase(Fx.DataSource, Options.Create(new WincheDatabaseOptions { QueryLimits = limits }));

    [Fact]
    public async Task QueryAsync_LimitOverCeiling_IsRejected()
    {
        var db = Core(new QueryLimits { MaxLimit = 50 });

        var ex = await Assert.ThrowsAsync<PlanValidationException>(
            () => db.QueryAsync(new Query("c") { Limit = 51 }));

        Assert.Equal("LIMIT_TOO_LARGE", ex.Code);
    }

    [Fact]
    public async Task QueryAsync_LimitAtCeiling_IsAccepted()
    {
        var db = Core(new QueryLimits { MaxLimit = 50 });

        var result = await db.QueryAsync(new Query("c") { Limit = 50 });

        Assert.NotNull(result);
    }

    [Fact]
    public async Task CountAsync_OffsetOverCeiling_IsRejected()
    {
        var db = Core(new QueryLimits { MaxOffset = 10 });

        await Assert.ThrowsAsync<PlanValidationException>(
            () => db.CountAsync(new Query("c") { Offset = 11 }));
    }

    [Fact]
    public async Task QueryAsync_TooManyOrderByKeys_IsRejected()
    {
        var db = Core(new QueryLimits { MaxOrderByKeys = 4 });
        var orders = Enumerable.Range(0, 5)
            .Select(i => new Ordering(FieldPath.Parse($"f{i}")))
            .ToList();

        var ex = await Assert.ThrowsAsync<PlanValidationException>(
            () => db.QueryAsync(new Query("c") { OrderBy = orders }));

        Assert.Equal("TOO_MANY_ORDER_BY_KEYS", ex.Code);
    }

    [Fact]
    public async Task QueryAsync_ArrayOperandOverCeiling_IsRejected()
    {
        var db = Core(new QueryLimits { MaxArrayOperandCount = 3 });
        var operand = new ArrayValue([.. Enumerable.Range(0, 4).Select(i => (Value)new IntegerValue(i))]);

        await Assert.ThrowsAsync<PlanValidationException>(
            () => db.QueryAsync(new Query("c",
                Where: new FieldFilter(FieldPath.Parse("x"), FilterOperator.In, operand))));
    }
}
