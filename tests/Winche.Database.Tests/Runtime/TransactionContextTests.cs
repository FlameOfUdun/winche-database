using Winche.Database.Documents;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Transactions;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.Tests.Runtime;

sealed class TxDouble : DatabaseTestDouble
{
    public int TxGets;
    public override Task<Document?> GetAsync(string transactionId, string path, CancellationToken ct = default)
    {
        TxGets++;
        return Task.FromResult<Document?>(null);
    }
}

public class TransactionContextTests
{
    private static TransactionContext Ctx(out TxDouble db)
    {
        db = new TxDouble();
        return new TestableContextFactory(db).Create("tx1");
    }

    // TransactionContext's ctor is internal — expose it to tests via a tiny factory in the same assembly?
    // No: the test project has InternalsVisibleTo, so construct directly:
    private sealed class TestableContextFactory(IDocumentDatabase db)
    {
        public TransactionContext Create(string id) => new(db, id);
    }

    [Fact]
    public async Task Reads_DelegateToTransactionalReads()
    {
        var ctx = Ctx(out var db);
        await ctx.GetAsync("c/a");
        await ctx.GetAllAsync(["c/b", "c/d"]);
        Assert.Equal(3, db.TxGets);
    }

    [Fact]
    public async Task ReadAfterWrite_Throws_InvalidArgument()
    {
        var ctx = Ctx(out _);
        ctx.Set("c/a", new Dictionary<string, Value>());
        var ex = await Assert.ThrowsAsync<RuntimeException>(() => ctx.GetAsync("c/a"));
        Assert.Equal(RuntimeStatus.InvalidArgument, ex.Status);
    }

    [Fact]
    public void Writes_BufferInOrder()
    {
        var ctx = Ctx(out _);
        ctx.Set("c/a", new Dictionary<string, Value>())
           .Update("c/a", new Dictionary<FieldPath, Value> { [FieldPath.Parse("x")] = new IntegerValue(1) })
           .Delete("c/b");
        Assert.Equal(3, ctx.BufferedWrites.Count);
        Assert.IsType<DeleteWrite>(ctx.BufferedWrites[2]);
    }
}
