using System.Collections.Concurrent;
using WincheDatabase.Core.Infrastructure;
using WincheDatabase.WS.Interfaces;

namespace WincheDatabase.WS.Services;

public sealed class TransactionConnectionMap : ITransactionConnectionMap
{
    private readonly ConcurrentDictionary<string, string> _ownerByTransaction = new(StringComparer.Ordinal);
    private readonly SecondaryIndexMap<string> _transactionsByConnection = new(StringComparer.Ordinal);

    public void Track(string connectionId, string transactionId)
    {
        _ownerByTransaction[transactionId] = connectionId;
        _transactionsByConnection.Add(connectionId, transactionId);
    }

    public bool TryGetOwner(string transactionId, out string? connectionId)
    {
        return _ownerByTransaction.TryGetValue(transactionId, out connectionId);
    }

    public void Untrack(string connectionId, string transactionId)
    {
        _ownerByTransaction.TryRemove(transactionId, out _);
        _transactionsByConnection.Remove(connectionId, transactionId);
    }

    public IReadOnlyList<string> UntrackAll(string connectionId)
    {
        var transactionIds = _transactionsByConnection.RemoveAll(connectionId);
        foreach (var txId in transactionIds)
            _ownerByTransaction.TryRemove(txId, out _);
        return transactionIds;
    }
}
