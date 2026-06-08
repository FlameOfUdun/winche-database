using Microsoft.Extensions.Options;
using Winche.Database.DependencyInjection;
using Winche.Database.Querying.Ast;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Transactions;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class OptimisticTransactionTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private DocumentDatabase Db(TransactionConfig? tx = null) => new(Fx.DataSource,
        Options.Create(new WincheDatabaseOptions { TransactionConfig = tx ?? new TransactionConfig() }));

    private static Dictionary<string, Value> Map(params (string K, Value V)[] e) => e.ToDictionary(x => x.K, x => x.V);

    [Fact]
    public async Task ReadThenCommit_NoConflict_Succeeds()
    {
        var db = Db();
        await db.WriteAsync([new SetWrite { Path = "c/a", Fields = Map(("n", new IntegerValue(1))) }]);

        var tx = await db.BeginTransactionAsync();
        var doc = await db.GetAsync(tx.Id, "c/a");
        await db.CommitTransactionAsync(tx.Id,
            [new SetWrite { Path = "c/a", Fields = Map(("n", new IntegerValue(((IntegerValue)doc!.Fields["n"]).Value + 1))) }]);

        Assert.Equal(new IntegerValue(2), (await db.GetAsync("c/a"))!.Fields["n"]);
    }

    [Fact]
    public async Task ConflictingWriteBetweenReadAndCommit_Aborts_NothingApplied()
    {
        var db = Db();
        await db.WriteAsync([new SetWrite { Path = "c/a", Fields = Map(("n", new IntegerValue(1))) }]);

        var tx = await db.BeginTransactionAsync();
        await db.GetAsync(tx.Id, "c/a");

        await db.WriteAsync([new SetWrite { Path = "c/a", Fields = Map(("n", new IntegerValue(99))) }]); // interloper

        await Assert.ThrowsAsync<TransactionAbortedException>(() => db.CommitTransactionAsync(tx.Id,
            [new SetWrite { Path = "c/a", Fields = Map(("n", new IntegerValue(2))) }]));
        Assert.Equal(new IntegerValue(99), (await db.GetAsync("c/a"))!.Fields["n"]);
    }

    [Fact]
    public async Task ReadMissing_ThenCreatedByOther_Aborts()
    {
        var db = Db();
        var tx = await db.BeginTransactionAsync();
        Assert.Null(await db.GetAsync(tx.Id, "c/ghost"));

        await db.WriteAsync([new SetWrite { Path = "c/ghost", Fields = Map() }]);    // interloper creates it

        await Assert.ThrowsAsync<TransactionAbortedException>(() =>
            db.CommitTransactionAsync(tx.Id, [new SetWrite { Path = "c/other", Fields = Map() }]));
    }

    [Fact]
    public async Task QueryInTransaction_RecordsReturnedDocs()
    {
        var db = Db();
        await db.WriteAsync(
        [
            new SetWrite { Path = "c/a", Fields = Map(("g", new IntegerValue(1))) },
            new SetWrite { Path = "c/b", Fields = Map(("g", new IntegerValue(1))) },
        ]);

        var tx = await db.BeginTransactionAsync();
        var result = await db.QueryAsync(tx.Id, new Query("c",
            Where: new FieldFilter(F("g"), FilterOperator.Eq, new IntegerValue(1))));
        Assert.Equal(2, result.Documents.Count);

        await db.WriteAsync([new SetWrite { Path = "c/b", Fields = Map(("g", new IntegerValue(2))) }]); // touch a returned doc

        await Assert.ThrowsAsync<TransactionAbortedException>(() =>
            db.CommitTransactionAsync(tx.Id, [new SetWrite { Path = "c/out", Fields = Map() }]));
    }

    [Fact]
    public async Task ReadOnlyCommit_ValidatesAtomically()
    {
        var db = Db();
        await db.WriteAsync([new SetWrite { Path = "c/a", Fields = Map() }]);

        var ok = await db.BeginTransactionAsync();
        await db.GetAsync(ok.Id, "c/a");
        Assert.Empty(await db.CommitTransactionAsync(ok.Id, []));            // fresh → succeeds, no writes

        var stale = await db.BeginTransactionAsync();
        await db.GetAsync(stale.Id, "c/a");
        await db.WriteAsync([new SetWrite { Path = "c/a", Fields = Map(("x", new IntegerValue(1))) }]);
        await Assert.ThrowsAsync<TransactionAbortedException>(() => db.CommitTransactionAsync(stale.Id, []));
    }

    [Fact]
    public async Task CommitConsumes_SecondCommitAborts_RollbackIdempotent()
    {
        var db = Db();
        var tx = await db.BeginTransactionAsync();
        await db.CommitTransactionAsync(tx.Id, [new SetWrite { Path = "c/once", Fields = Map() }]);
        await Assert.ThrowsAsync<TransactionAbortedException>(() =>
            db.CommitTransactionAsync(tx.Id, [new SetWrite { Path = "c/twice", Fields = Map() }]));
        await db.RollbackTransactionAsync(tx.Id);                            // no throw
    }

    [Fact]
    public async Task ExpiredTransaction_Aborts()
    {
        var db = Db(new TransactionConfig { TotalTimeoutSpan = TimeSpan.FromMilliseconds(50), IdleTimeoutSpan = TimeSpan.FromMilliseconds(50) });
        var tx = await db.BeginTransactionAsync();
        await Task.Delay(150);
        await Assert.ThrowsAsync<TransactionAbortedException>(() =>
            db.CommitTransactionAsync(tx.Id, [new SetWrite { Path = "c/late", Fields = Map() }]));
    }

    [Fact]
    public async Task RereadOfChangedDoc_AbortsAtReadTime()
    {
        var db = Db();
        await db.WriteAsync([new SetWrite { Path = "c/a", Fields = Map() }]);

        var tx = await db.BeginTransactionAsync();
        await db.GetAsync(tx.Id, "c/a");
        await db.WriteAsync([new SetWrite { Path = "c/a", Fields = Map(("x", new IntegerValue(1))) }]);
        await Assert.ThrowsAsync<TransactionAbortedException>(() => db.GetAsync(tx.Id, "c/a"));
    }

    [Fact]
    public async Task ReadAndWriteSamePath_InterloperBetween_Aborts()
    {
        // the strongest invariant: a path BOTH read and written validates against the read
        // version under the same lock as the write — an interloper in between must abort.
        var db = Db();
        await db.WriteAsync([new SetWrite { Path = "c/rw", Fields = Map(("n", new IntegerValue(1))) }]);

        var tx = await db.BeginTransactionAsync();
        await db.GetAsync(tx.Id, "c/rw");

        await db.WriteAsync([new SetWrite { Path = "c/rw", Fields = Map(("n", new IntegerValue(99))) }]);

        await Assert.ThrowsAsync<TransactionAbortedException>(() => db.CommitTransactionAsync(tx.Id,
            [new SetWrite { Path = "c/rw", Fields = Map(("n", new IntegerValue(2))) }]));
        Assert.Equal(new IntegerValue(99), (await db.GetAsync("c/rw"))!.Fields["n"]);
    }
}
