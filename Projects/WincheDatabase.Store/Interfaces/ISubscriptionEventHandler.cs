using WincheDatabase.Store.Models;

namespace WincheDatabase.Store.Interfaces;

public interface ISubscriptionEventHandler
{
    Task HandleAsync(List<SubscriptionEvent> events, CancellationToken ct);
}
