using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Winche.Database.Documents;
using Winche.Database.Models;
using Winche.Database.Querying.Ast;
using Winche.Database.Services;
using Winche.Database.Values;
using Winche.Sentinel.Interfaces;
using Winche.Sentinel.Models;

namespace Winche.Database.IntegrationTests;

file sealed class TxAllowAll : IAccessRuleEvaluator<Document>
{
    public Task EvaluateAsync(AccessOperation operation, string path, object? data,
        Func<CancellationToken, Task<Document?>>? getResource, CancellationToken ct = default) => Task.CompletedTask;
}

[Collection("postgres")]
public class TransactionTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private TransactionManager Manager() => new(
        Options.Create(new StoreOptions { TableName = fx.Table }),
        new TxAllowAll(),
        fx.DataSource,
        new TransactionRegistry());

    [Fact]
    public async Task SetInsideTx_VisibleInsideTx_ThenRollback_DocGone()
    {
        var mgr = Manager();
        var tx = await mgr.BeginAsync();
        await using (tx)
        {
            await tx.SetAsync("txtest/r1", new Dictionary<string, Value> { ["v"] = new IntegerValue(42) });

            // Visible inside the same transaction
            var inside = await tx.GetAsync("txtest/r1");
            Assert.NotNull(inside);
            Assert.Equal(new IntegerValue(42), inside.Fields["v"]);

            await tx.RollbackAsync();
        }

        // After rollback, doc must not exist
        await using var conn = await fx.DataSource.OpenConnectionAsync();
        var ops = new DocumentOperations(conn, null, fx.Table);
        var afterRollback = await ops.GetAsync("txtest/r1");
        Assert.Null(afterRollback);
    }

    [Fact]
    public async Task SetInsideTx_Commit_DocVisibleOutside()
    {
        var mgr = Manager();
        var tx = await mgr.BeginAsync();
        await using (tx)
        {
            await tx.SetAsync("txtest/r2", new Dictionary<string, Value> { ["v"] = new IntegerValue(99) });
            await tx.CommitAsync();
        }

        // After commit, doc must be visible outside
        await using var conn = await fx.DataSource.OpenConnectionAsync();
        var ops = new DocumentOperations(conn, null, fx.Table);
        var afterCommit = await ops.GetAsync("txtest/r2");
        Assert.NotNull(afterCommit);
        Assert.Equal(new IntegerValue(99), afterCommit.Fields["v"]);
    }

    [Fact]
    public async Task DisposeWithoutCommit_RollsBack()
    {
        var mgr = Manager();
        {
            var tx = await mgr.BeginAsync();
            await using (tx)
            {
                await tx.SetAsync("txtest/r3", new Dictionary<string, Value> { ["v"] = new IntegerValue(7) });
                // No commit — dispose should roll back
            }
        }

        await using var conn = await fx.DataSource.OpenConnectionAsync();
        var ops = new DocumentOperations(conn, null, fx.Table);
        var result = await ops.GetAsync("txtest/r3");
        Assert.Null(result);
    }
}
