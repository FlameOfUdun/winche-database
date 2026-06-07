using Winche.Database.Interfaces;
using Winche.Database.Models;
using Winche.Database.Querying;
using Winche.Database.Querying.Ast;

namespace Winche.Database.Services;

public sealed class SubscriptionManager(
    ISubscriptionRegistry registry,
    IDocumentManager manager
) : ISubscriptionManager
{
    public async Task<QuerySubscription> SubscribeAsync(QueryAst query, CancellationToken ct = default)
    {
        var result = await manager.QueryAsync(query, ct);
        var snapshot = new QuerySnapshot
        {
            DocumentIds = [.. result.Documents.Select(d => d.Id)],
        };

        var id = Guid.NewGuid().ToString();
        var groupKey = QueryKey.Compute(query);
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
