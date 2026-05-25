using Winche.Database.AST.Models;
using Winche.Database.Models;

namespace Winche.Database.Interfaces;

public interface ISubscriptionManager
{
    Task<QuerySubscription> SubscribeAsync(Query query, CancellationToken ct = default);
    bool Unsubscribe(string subscriptionId);
}
