using Winche.Database.Models;
using Winche.Database.Querying.Ast;

namespace Winche.Database.Interfaces;

public interface ISubscriptionRegistry
{
    QueryGroup AddSubscription(string subscriptionId, QueryAst query, QuerySnapshot snapshot, string groupKey);
    bool TryRemove(string subscriptionId);
    IEnumerable<QueryGroup> GetGroupsByCollection(string collection);
    QueryGroup? TryGetGroup(string groupKey);
    IEnumerable<string> GetSubscriptionIds(string groupKey);
    bool TryUpdateGroupSnapshot(string groupKey, QuerySnapshot expected, QuerySnapshot newSnapshot);
    int SubscriptionCount { get; }
    int GroupCount { get; }
}
