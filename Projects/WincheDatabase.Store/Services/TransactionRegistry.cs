using System.Collections.Concurrent;
using WincheDatabase.Store.Abstraction;
using WincheDatabase.Store.Operands;

namespace WincheDatabase.Store.Services;

public sealed class TransactionRegistry : ITransactionRegistry, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Transaction> _transactions = new(StringComparer.Ordinal);

    public bool TryAdd(string id, Transaction tx)
    {
        return _transactions.TryAdd(id, tx);
    }

    public bool TryGet(string id, out Transaction? tx)
    {
        return _transactions.TryGetValue(id, out tx);
    }

    public bool TryRemove(string id, out Transaction? tx)
    {
        return _transactions.TryRemove(id, out tx);
    }

    public async Task RemoveExpiredAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        foreach (var (id, tx) in _transactions)
        {
            var isTotalExpired = tx.CreatedAt < now - timeout;
            var isIdleExpired = tx.LastActivityAt < now - timeout;
            var isCompleted = tx.IsCompleted;

            if (!isTotalExpired && !isIdleExpired && !isCompleted)
                continue;

            if (!_transactions.TryRemove(id, out _))
                continue;

            await RollbackAndDisposeAsync(tx, ct);
        }
    }

    public int Count => _transactions.Count;

    private static async Task RollbackAndDisposeAsync(Transaction tx, CancellationToken ct)
    {
        if (!tx.IsCompleted)
        {
            try
            {
                await tx.RollbackAsync(ct);
            }
            catch
            {
                /* connection already lost or tx already completed */
            }
        }

        await tx.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        var all = _transactions.Values.ToList();

        _transactions.Clear();

        foreach (var tx in all)
        {
            await RollbackAndDisposeAsync(tx, CancellationToken.None);
        }
    }
}
