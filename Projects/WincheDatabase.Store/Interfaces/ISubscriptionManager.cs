using WincheDatabase.AST.Models;
using WincheDatabase.Store.Models;

namespace WincheDatabase.Store.Interfaces;

public interface ISubscriptionManager
{
    Task<QuerySubscription> SubscribeAsync(Query query, CancellationToken ct = default);
    bool Unsubscribe(string subscriptionId);
}
