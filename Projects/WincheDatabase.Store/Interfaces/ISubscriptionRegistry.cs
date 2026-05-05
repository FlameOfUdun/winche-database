using WincheDatabase.AST.Models;
using WincheDatabase.Store.Models;

namespace WincheDatabase.Store.Interfaces;

public interface ISubscriptionRegistry
{
    QueryGroup AddSubscription(string subscriptionId, Query query, QuerySnapshot snapshot, string groupKey);
    bool TryRemove(string subscriptionId);
    IEnumerable<QueryGroup> GetGroupsByCollection(string collection);
    QueryGroup? TryGetGroup(string groupKey);
    IEnumerable<string> GetSubscriptionIds(string groupKey);
    bool TryUpdateGroupSnapshot(string groupKey, QuerySnapshot expected, QuerySnapshot newSnapshot);
    int SubscriptionCount { get; }
    int GroupCount { get; }
}
