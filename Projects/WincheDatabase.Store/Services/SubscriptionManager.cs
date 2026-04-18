using WincheDatabase.AST.Models;
using WincheDatabase.Store.Abstraction;
using WincheDatabase.Store.Infrastructure;
using WincheDatabase.Store.Models;

namespace WincheDatabase.Store.Services;

public sealed class SubscriptionManager(
    ISubscriptionRegistry registry,
    IDocumentManager manager
) : ISubscriptionManager
{
    public async Task<QuerySubscription> SubscribeAsync(Query query, CancellationToken ct = default)
    {
        var result = await manager.QueryAsync(query, ct);
        var snapshot = new QuerySnapshot
        {
            DocumentIds = [.. result.Documents.Select(d => d.Id)],
        };

        var id = Guid.NewGuid().ToString();
        var groupKey = QuerySerializer.Serialize(query);
        registry.AddSubscription(id, query, snapshot, groupKey);

        return new QuerySubscription
        {
            Id = id,
            Result = result,
        };
    }

    public bool Unsubscribe(string subscriptionId)
    {
        return registry.TryRemove(subscriptionId);
    }
}
