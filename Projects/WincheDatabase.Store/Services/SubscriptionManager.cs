using WincheDatabase.AST.Models;
using WincheDatabase.Store.Infrastructure;
using WincheDatabase.Store.Models;
using WincheDatabase.Store.Stores;

namespace WincheDatabase.Store.Services;

public sealed class SubscriptionManager(
    SubscriptionRegistry registry,
    DocumentManager manager
)
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
