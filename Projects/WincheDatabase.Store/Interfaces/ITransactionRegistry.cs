using WincheDatabase.Store.Operands;

namespace WincheDatabase.Store.Interfaces;

public interface ITransactionRegistry
{
    public bool TryAdd(string id, Transaction tx);
    public bool TryGet(string id, out Transaction? tx);
    bool TryRemove(string id, out Transaction? tx);
    Task RemoveExpiredAsync(TimeSpan timeout, CancellationToken ct = default);
    int Count { get; }
}
