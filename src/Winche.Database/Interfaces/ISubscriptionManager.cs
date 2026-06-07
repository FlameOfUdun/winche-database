using Winche.Database.Models;
using Winche.Database.Querying.Ast;

namespace Winche.Database.Interfaces;

public interface ISubscriptionManager
{
    Task<QuerySubscription> SubscribeAsync(QueryAst query, CancellationToken ct = default);
    bool Unsubscribe(string subscriptionId);
}
