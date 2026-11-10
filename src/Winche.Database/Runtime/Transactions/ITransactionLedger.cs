using Winche.Database.DependencyInjection;

namespace Winche.Database.Runtime.Transactions;

/// <summary>
/// Where optimistic transactions keep their read sets between requests. Expired or unknown ids abort.
/// </summary>
internal interface ITransactionLedger
{
    Task<string> BeginAsync(CancellationToken ct);

    /// <summary>Records a read; aborts (and ends the transaction) when the same path was read at another version.</summary>
    Task RecordReadAsync(string id, string path, DateTimeOffset? updateTime, CancellationToken ct);

    /// <summary>Returns the read set and ends the transaction (commit consumes it).</summary>
    Task<IReadOnlyDictionary<string, DateTimeOffset?>> ConsumeAsync(string id, CancellationToken ct);

    /// <summary>Idempotent: unknown ids are a no-op.</summary>
    Task RollbackAsync(string id, CancellationToken ct);

    Task<int> RemoveExpiredAsync(CancellationToken ct);

    Task<int> CountAsync(CancellationToken ct);
}

/// <summary>The per-node, in-memory ledger (<see cref="TransactionStore.Memory"/>).</summary>
internal sealed class MemoryTransactionLedger(TransactionConfig config) : ITransactionLedger
{
    private readonly TransactionLedger _ledger = new(config);

    public Task<string> BeginAsync(CancellationToken ct) => Task.FromResult(_ledger.Begin());

    public Task RecordReadAsync(string id, string path, DateTimeOffset? updateTime, CancellationToken ct)
    {
        _ledger.RecordRead(id, path, updateTime);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, DateTimeOffset?>> ConsumeAsync(string id, CancellationToken ct) =>
        Task.FromResult(_ledger.Consume(id));

    public Task RollbackAsync(string id, CancellationToken ct)
    {
        _ledger.Rollback(id);
        return Task.CompletedTask;
    }

    public Task<int> RemoveExpiredAsync(CancellationToken ct) => Task.FromResult(_ledger.RemoveExpired());

    public Task<int> CountAsync(CancellationToken ct) => Task.FromResult(_ledger.Count);
}
