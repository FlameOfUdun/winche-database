using Microsoft.Extensions.Options;
using Winche.Database.Models;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Transactions;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class RunTransactionTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private DocumentDatabase Db() => new(Fx.DataSource,
        Options.Create(new StoreOptions { TableName = Fx.Table }));

    private static Dictionary<string, Value> Map(params (string K, Value V)[] e) => e.ToDictionary(x => x.K, x => x.V);

    [Fact]
    public async Task ContendedReadModifyWrite_ConvergesViaRetries()
    {
        var db = Db();
        await db.WriteAsync([new SetWrite { Path = "c/counter", Fields = Map(("n", new IntegerValue(0))) }]);

        const int N = 12;
        await Task.WhenAll(Enumerable.Range(0, N).Select(_ => db.RunTransactionAsync(async ctx =>
        {
            var doc = await ctx.GetAsync("c/counter");
            var n = ((IntegerValue)doc!.Fields["n"]).Value;
            ctx.Set("c/counter", Map(("n", new IntegerValue(n + 1))));
            return n;
        }, new TransactionOptions(MaxAttempts: 30))));

        Assert.Equal(new IntegerValue(N), (await db.GetAsync("c/counter"))!.Fields["n"]);
    }

    [Fact]
    public async Task BodyException_RollsBack_AndRethrows()
    {
        var db = Db();
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.RunTransactionAsync<bool>(async ctx =>
        {
            await ctx.GetAsync("c/a");
            ctx.Set("c/should-not-exist", Map());
            throw new InvalidOperationException("boom");
        }));
        Assert.Null(await db.GetAsync("c/should-not-exist"));
        Assert.Equal(0, db.Ledger.Count);                                    // no leaked entries
    }

    [Fact]
    public async Task RetriesExhausted_ThrowsLastAborted()
    {
        var db = Db();
        await db.WriteAsync([new SetWrite { Path = "c/hot", Fields = Map(("n", new IntegerValue(0))) }]);

        // every attempt reads, then we sabotage before its commit by writing out-of-band
        var ex = await Assert.ThrowsAsync<TransactionAbortedException>(() => db.RunTransactionAsync(async ctx =>
        {
            var doc = await ctx.GetAsync("c/hot");
            await db.WriteAsync([new SetWrite { Path = "c/hot", Fields = Map(("n", new IntegerValue(-1))) }]);
            ctx.Set("c/hot", Map(("n", new IntegerValue(123))));
            return true;
        }, new TransactionOptions(MaxAttempts: 3)));
        Assert.Equal(RuntimeStatus.Aborted, ex.Status);
    }

    [Fact]
    public async Task ReadOnlyBody_CommitsCleanly()
    {
        var db = Db();
        await db.WriteAsync([new SetWrite { Path = "c/a", Fields = Map(("n", new IntegerValue(7))) }]);

        var n = await db.RunTransactionAsync(async ctx =>
            ((IntegerValue)(await ctx.GetAsync("c/a"))!.Fields["n"]).Value);
        Assert.Equal(7, n);
    }
}
