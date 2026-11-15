using Microsoft.Extensions.Options;
using Winche.Database.DependencyInjection;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Transactions;
using Winche.Database.Runtime.Writes;
using Winche.Values;

namespace Winche.Database.IntegrationTests;

/// <summary>
/// 10.5.0: transactions live in Postgres, so each step can land on a different node (REST behind a load
/// balancer, no sticky routing). Separate DocumentDatabase instances stand in for separate nodes.
/// </summary>
[Collection("postgres")]
public class SharedTransactionTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private DocumentDatabase Node(TransactionConfig? config = null) =>
        new(Fx.DataSource, Options.Create(new WincheDatabaseOptions { TransactionConfig = config ?? new TransactionConfig() }));

    private static Dictionary<string, Value> N(long n) => new() { ["n"] = new IntegerValue(n) };

    [Fact]
    public async Task Begin_read_and_commit_on_three_different_nodes()
    {
        await Node().WriteAsync([new SetWrite { Path = "acct/a", Fields = N(100) }]);
        var (a, b, c) = (Node(), Node(), Node());

        var tx = await a.BeginTransactionAsync();
        var doc = await b.GetAsync(tx.Id, "acct/a");
        var missing = await b.GetAsync(tx.Id, "acct/none");
        Assert.Null(missing);
        await c.CommitTransactionAsync(tx.Id, [
            new SetWrite { Path = "acct/a", Fields = N(((IntegerValue)doc!.Fields["n"]).Value - 30) },
            new SetWrite { Path = "acct/b", Fields = N(30) },
        ]);

        Assert.Equal(new IntegerValue(70), (await a.GetAsync("acct/a"))!.Fields["n"]);
        Assert.Equal(0, await a.Ledger.CountAsync(default));   // consumed
    }

    [Fact]
    public async Task A_change_between_read_and_commit_aborts_whichever_node_commits()
    {
        await Node().WriteAsync([new SetWrite { Path = "acct/a", Fields = N(1) }]);
        var tx = await Node().BeginTransactionAsync();
        await Node().GetAsync(tx.Id, "acct/a");
        await Node().WriteAsync([new SetWrite { Path = "acct/a", Fields = N(2) }]);   // someone else

        await Assert.ThrowsAsync<TransactionAbortedException>(() =>
            Node().CommitTransactionAsync(tx.Id, [new SetWrite { Path = "acct/a", Fields = N(3) }]));
        Assert.Equal(new IntegerValue(2), (await Node().GetAsync("acct/a"))!.Fields["n"]);
    }

    [Fact]
    public async Task Reading_a_path_again_at_another_version_aborts()
    {
        await Node().WriteAsync([new SetWrite { Path = "acct/a", Fields = N(1) }]);
        var tx = await Node().BeginTransactionAsync();
        await Node().GetAsync(tx.Id, "acct/a");
        await Node().GetAsync(tx.Id, "acct/a");   // same version: fine
        await Node().WriteAsync([new SetWrite { Path = "acct/a", Fields = N(2) }]);

        await Assert.ThrowsAsync<TransactionAbortedException>(() => Node().GetAsync(tx.Id, "acct/a"));
        await Assert.ThrowsAsync<TransactionAbortedException>(() => Node().CommitTransactionAsync(tx.Id, []));   // ended
    }

    [Fact]
    public async Task Rollback_and_expiry_end_it_everywhere()
    {
        var tx = await Node().BeginTransactionAsync();
        await Node().RollbackTransactionAsync(tx.Id);
        await Node().RollbackTransactionAsync(tx.Id);   // idempotent
        await Assert.ThrowsAsync<TransactionAbortedException>(() => Node().GetAsync(tx.Id, "acct/a"));

        var quick = new TransactionConfig { IdleTimeoutSpan = TimeSpan.FromMilliseconds(100) };
        var idle = await Node(quick).BeginTransactionAsync();
        await Task.Delay(300);
        await Assert.ThrowsAsync<TransactionAbortedException>(() => Node(quick).GetAsync(idle.Id, "acct/a"));
    }

    [Fact]
    public async Task RunTransaction_retries_across_the_shared_ledger()
    {
        await Node().WriteAsync([new SetWrite { Path = "ctr/c", Fields = N(0) }]);
        var nodes = Enumerable.Range(0, 4).Select(_ => Node()).ToList();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => nodes[i % 4].RunTransactionAsync(async t =>
        {
            var d = await t.GetAsync("ctr/c");
            t.Set("ctr/c", N(((IntegerValue)d!.Fields["n"]).Value + 1));
            return 0;
        }, new TransactionOptions(MaxAttempts: 50))));
        Assert.Equal(new IntegerValue(20), (await Node().GetAsync("ctr/c"))!.Fields["n"]);
    }

    [Fact]
    public async Task The_memory_store_still_keeps_transactions_per_node()
    {
        var memory = new TransactionConfig { Store = TransactionStore.Memory };
        var a = Node(memory);
        var tx = await a.BeginTransactionAsync();
        await Assert.ThrowsAsync<TransactionAbortedException>(() => Node(memory).GetAsync(tx.Id, "acct/a"));
        await a.RollbackTransactionAsync(tx.Id);
        Assert.Equal(0, await Node().Ledger.CountAsync(default));   // nothing written to the table
    }
}
