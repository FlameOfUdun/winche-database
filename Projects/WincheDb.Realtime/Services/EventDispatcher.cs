using WincheDb.DocumentStore.Abstraction;
using WincheDb.DocumentStore.Models;
using WincheDb.Realtime.Messages;
using WincheDb.Realtime.Stores;

namespace WincheDb.Realtime.Services;

internal sealed class EventDispatcher(
    SubscriptionConnectionMap subscriptionConnectionMap,
    ConnectionRegistry connectionRegistry
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
