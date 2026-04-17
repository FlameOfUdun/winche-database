using System.Collections.Concurrent;
using WincheDatabase.Core.Infrastructure;

namespace WincheDatabase.WS.Stores;

public sealed class SubscriptionConnectionMap
{
    private readonly ConcurrentDictionary<string, string> _ownerBySubscription = new(StringComparer.Ordinal);
    private readonly SecondaryIndexMap<string> _subscriptionsByConnection = new(StringComparer.Ordinal);

    public void Track(string connectionId, string subscriptionId)
    {
        _ownerBySubscription[subscriptionId] = connectionId;
        _subscriptionsByConnection.Add(connectionId, subscriptionId);
    }

    public bool TryGetOwner(string subscriptionId, out string? connectionId)
    {
        return _ownerBySubscription.TryGetValue(subscriptionId, out connectionId);
    }

    public void Untrack(string connectionId, string subscriptionId)
    {
        _ownerBySubscription.TryRemove(subscriptionId, out _);
        _subscriptionsByConnection.Remove(connectionId, subscriptionId);
    }

    public IReadOnlyList<string> UntrackAll(string connectionId)
    {
        var subscriptionIds = _subscriptionsByConnection.RemoveAll(connectionId);
        foreach (var subId in subscriptionIds)
            _ownerBySubscription.TryRemove(subId, out _);
        return subscriptionIds;
    }
}
