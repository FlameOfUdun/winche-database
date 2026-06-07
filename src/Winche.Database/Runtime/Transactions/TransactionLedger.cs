using System.Collections.Concurrent;
using Winche.Database.Models;

namespace Winche.Database.Runtime.Transactions;

/// <summary>
/// The optimistic transaction registry (spec §3): per-node, in-memory, no DB resources.
/// Every access checks idle/absolute expiry; expired/unknown ids → ABORTED. All `now`
/// parameters default to the wall clock and exist for deterministic tests.
/// </summary>
public sealed class TransactionLedger(TransactionConfig config)
{
    private sealed class Entry
    {
        public readonly Dictionary<string, DateTimeOffset?> ReadSet = new(StringComparer.Ordinal);
        public DateTimeOffset CreatedAt;
        public DateTimeOffset LastActivity;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public string Begin(DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        var id = Guid.NewGuid().ToString("N");
        _entries[id] = new Entry { CreatedAt = t, LastActivity = t };
        return id;
    }

    public void RecordRead(string id, string path, DateTimeOffset? updateTime, DateTimeOffset? now = null)
    {
        var entry = Alive(id, now ?? DateTimeOffset.UtcNow);
        lock (entry)
        {
            if (entry.ReadSet.TryGetValue(path, out var recorded))
            {
                if (recorded != updateTime)
                {
                    _entries.TryRemove(id, out _);
                    throw new TransactionAbortedException($"Document '{path}' changed during the transaction.");
                }
                return;
            }
            entry.ReadSet[path] = updateTime;
        }
    }

    /// <summary>Returns the read set and removes the entry (commit consumes the transaction).</summary>
    public IReadOnlyDictionary<string, DateTimeOffset?> Consume(string id, DateTimeOffset? now = null)
    {
        Alive(id, now ?? DateTimeOffset.UtcNow);
        if (!_entries.TryRemove(id, out var entry))
            throw new TransactionAbortedException($"Transaction '{id}' is not active.");
        lock (entry) return new Dictionary<string, DateTimeOffset?>(entry.ReadSet);
    }

    /// <summary>Idempotent: unknown ids are a no-op.</summary>
    public void Rollback(string id) => _entries.TryRemove(id, out _);

    /// <summary>For the Plan-3 sweeper. Correctness never depends on it (Alive checks inline).</summary>
    public int RemoveExpired(DateTimeOffset? now = null)
    {
        var t = now ?? DateTimeOffset.UtcNow;
        var removed = 0;
        foreach (var (id, entry) in _entries)
            if (IsExpired(entry, t) && _entries.TryRemove(id, out _))
                removed++;
        return removed;
    }

    public int Count => _entries.Count;

    private Entry Alive(string id, DateTimeOffset now)
    {
        if (!_entries.TryGetValue(id, out var entry))
            throw new TransactionAbortedException($"Transaction '{id}' is not active.");
        if (IsExpired(entry, now))
        {
            _entries.TryRemove(id, out _);
            throw new TransactionAbortedException($"Transaction '{id}' has expired.");
        }
        entry.LastActivity = now;
        return entry;
    }

    private bool IsExpired(Entry entry, DateTimeOffset now) =>
        now - entry.LastActivity > config.IdleTimeoutSpan
        || now - entry.CreatedAt > config.TotalTimeoutSpan;
}
