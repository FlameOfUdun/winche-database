using Winche.Database.Models;

namespace Winche.Database.Interfaces;

public interface ISubscriptionEventHandler
{
    Task HandleAsync(List<SubscriptionEvent> events, CancellationToken ct);
}
