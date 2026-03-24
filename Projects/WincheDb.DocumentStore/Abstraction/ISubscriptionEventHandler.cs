using WincheDb.DocumentStore.Models;

namespace WincheDb.DocumentStore.Abstraction;

public interface ISubscriptionEventHandler
{
    Task HandleAsync(List<SubscriptionEvent> events, CancellationToken ct);
}
