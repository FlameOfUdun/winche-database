using WincheDatabase.Store.Models;

namespace WincheDatabase.Store.Abstraction;

public interface ISubscriptionEventHandler
{
    Task HandleAsync(List<SubscriptionEvent> events, CancellationToken ct);
}
