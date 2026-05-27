using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Winche.Sentinel.Interfaces;
using Winche.Database.Interfaces;
using Winche.Database.Core.Models;
using Winche.Database.Constants;
using Winche.Database.Models;
using Winche.Database.Operands;

namespace Winche.Database.Services;

public sealed class TransactionManager(
    IOptions<StoreOptions> options,
    IAccessRuleEvaluator<Document> evaluator,
    [FromKeyedServices(ServiceKeys.DATA_SOURCE_KEY)] NpgsqlDataSource source,
    ITransactionRegistry registry
) : ITransactionManager
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
}
