using System.Collections.Concurrent;

namespace WincheDatabase.Core.Infrastructure;

/// <summary>
/// Thread-safe secondary index mapping keys to sets of item IDs.
/// Provides O(1) lookup, add, and remove operations.
/// Automatically cleans up empty buckets on removal.
/// </summary>
public sealed class SecondaryIndexMap<TKey>(IEqualityComparer<TKey>? keyComparer = null) where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, ConcurrentDictionary<string, byte>> _index = new(keyComparer ?? EqualityComparer<TKey>.Default);

    /// <summary>
    /// Adds an item ID under a key. Idempotent - adding the same ID twice is a no-op.
    /// </summary>
    public void Add(TKey key, string id)
    {
        _index
            .GetOrAdd(key, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal))
            .TryAdd(id, 0);
    }

    /// <summary>
    /// Removes an item ID from a key. Automatically cleans up empty buckets.
    /// </summary>
    public void Remove(TKey key, string id)
    {
        if (!_index.TryGetValue(key, out var ids))
            return;

        ids.TryRemove(id, out _);

        if (ids.IsEmpty)
            _index.TryRemove(key, out _);
    }

    /// <summary>
    /// Gets all item IDs for a key. Returns empty enumerable if key not found.
    /// </summary>
    public IEnumerable<string> GetIds(TKey key)
    {
        return _index.TryGetValue(key, out var ids) ? ids.Keys : [];
    }

    /// <summary>
    /// Removes all item IDs for a key and returns them.
    /// </summary>
    public IReadOnlyList<string> RemoveAll(TKey key)
    {
        if (!_index.TryRemove(key, out var ids))
            return [];

        return [.. ids.Keys];
    }

    /// <summary>
    /// Returns the count of item IDs under a key.
    /// </summary>
    public int Count(TKey key)
    {
        return _index.TryGetValue(key, out var ids) ? ids.Count : 0;
    }

    /// <summary>
    /// Clears all entries from the index.
    /// </summary>
    public void Clear()
    {
        _index.Clear();
    }
}
