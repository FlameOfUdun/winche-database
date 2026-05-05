using WincheDatabase.Store.Interfaces;
using WincheDatabase.Store.Models;
using WincheDatabase.WS.Interfaces;
using WincheDatabase.WS.Messages;

namespace WincheDatabase.WS.Services;

public sealed class EventDispatcher(
    ISubscriptionConnectionMap subscriptionConnectionMap,
    IConnectionRegistry connectionRegistry
) : ISubscriptionEventHandler
{
    public async Task HandleAsync(List<SubscriptionEvent> events, CancellationToken ct)
    {
        var byConnection = new Dictionary<string, List<QueryUpdateNotification>>(StringComparer.Ordinal);

        foreach (var ev in events)
        {
            if (!subscriptionConnectionMap.TryGetOwner(ev.SubscriptionId, out var connectionId) || connectionId is null)
                continue;

            if (!byConnection.TryGetValue(connectionId, out var list))
            {
                list = [];
                byConnection[connectionId] = list;
            }

            list.Add(new QueryUpdateNotification
            {
                SubscriptionId = ev.SubscriptionId,
                Change = ev.Change,
            });
        }

        foreach (var (connectionId, messages) in byConnection)
            await connectionRegistry.SendBatchAsync(connectionId, messages, ct);
    }
}
