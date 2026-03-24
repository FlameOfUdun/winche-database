using System.Collections.Concurrent;
using WincheDb.Core.Ast;
using WincheDb.Core.Infrastructure;
using WincheDb.DocumentStore.Models;

namespace WincheDb.DocumentStore.Stores;

public sealed class SubscriptionRegistry
{
    // group key → group data (query, shared snapshot)
    private readonly ConcurrentDictionary<string, QueryGroup> _groups = new(StringComparer.Ordinal);

    // collection → group keys
    private readonly SecondaryIndexMap<string> _byCollection = new(StringComparer.OrdinalIgnoreCase);

    // group key → subscription IDs
    private readonly SecondaryIndexMap<string> _byGroup = new(StringComparer.Ordinal);

    // subscription ID → group key (reverse lookup)
    private readonly ConcurrentDictionary<string, string> _subscriptionToGroup = new(StringComparer.Ordinal);

    public QueryGroup AddSubscription(string subscriptionId, Query query, QuerySnapshot snapshot, string groupKey)
    {
        var group = _groups.GetOrAdd(groupKey, _ =>
        {
            _byCollection.Add(query.Collection, groupKey);
            return new QueryGroup
            {
                Key = groupKey,
                Collection = query.Collection,
                Query = query,
                Snapshot = snapshot,
            };
        });

        _byGroup.Add(groupKey, subscriptionId);
        _subscriptionToGroup[subscriptionId] = groupKey;

        return group;
    }

    public bool TryRemove(string subscriptionId)
    {
        if (!_subscriptionToGroup.TryRemove(subscriptionId, out var groupKey))
            return false;

        _byGroup.Remove(groupKey, subscriptionId);

        // If group is now empty, clean it up
        if (_byGroup.Count(groupKey) == 0)
        {
            if (_groups.TryRemove(groupKey, out var group))
                _byCollection.Remove(group.Collection, groupKey);
        }

        return true;
    }

    public IEnumerable<QueryGroup> GetGroupsByCollection(string collection)
    {
        foreach (var groupKey in _byCollection.GetIds(collection))
        {
            if (_groups.TryGetValue(groupKey, out var group))
                yield return group;
        }
    }

    public QueryGroup? TryGetGroup(string groupKey)
    {
        return _groups.TryGetValue(groupKey, out var group) ? group : null;
    }

    public IEnumerable<string> GetSubscriptionIds(string groupKey)
    {
        return _byGroup.GetIds(groupKey);
    }

    public bool TryUpdateGroupSnapshot(string groupKey, QuerySnapshot expected, QuerySnapshot newSnapshot)
    {
        if (!_groups.TryGetValue(groupKey, out var group))
            return false;

        // CAS on snapshot reference
        return Interlocked.CompareExchange(ref group.Snapshot, newSnapshot, expected) == expected;
    }

    public int SubscriptionCount => _subscriptionToGroup.Count;
    public int GroupCount => _groups.Count;
}
