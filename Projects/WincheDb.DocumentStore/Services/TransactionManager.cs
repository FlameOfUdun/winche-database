using Npgsql;
using WincheDb.DocumentStore.Operands;
using WincheDb.DocumentStore.Stores;

namespace WincheDb.DocumentStore.Services;

public sealed class TransactionManager(
    StoreOptions options,
    NpgsqlDataSource source,
    TransactionRegistry registry
)
{
    public async Task<Transaction> BeginAsync(CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString();
        var connection = await source.OpenConnectionAsync(ct);
        var transaction = await connection.BeginTransactionAsync(ct);
        var tx = new Transaction(id, options, connection, transaction);
        if (!registry.TryAdd(id, tx))
        {
            await tx.DisposeAsync();
            throw new InvalidOperationException("Failed to register the transaction.");
        }
        return tx;
    }

    public bool TryGet(string id, out Transaction? tx)
    {
        return registry.TryGet(id, out tx);
    }
}
