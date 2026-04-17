using Microsoft.Extensions.Options;
using Npgsql;
using WincheDatabase.Store;
using WincheDatabase.Store.Operands;
using WincheDatabase.Store.Stores;

namespace WincheDatabase.Store.Services;

public sealed class TransactionManager(
    IOptions<StoreOptions> options,
    AccessRuleEvaluator evaluator,
    NpgsqlDataSource source,
    TransactionRegistry registry
)
{
    public async Task<Transaction> BeginAsync(CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString();
        var connection = await source.OpenConnectionAsync(ct);
        var transaction = await connection.BeginTransactionAsync(ct);
        var tx = new Transaction(id, options, evaluator, connection, transaction);
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
