using Winche.Database.Models;

namespace Winche.Database.Interfaces;

public interface IEventChannel
{
    Task WriteAsync(List<SubscriptionEvent> events, CancellationToken ct = default);
    IAsyncEnumerable<List<SubscriptionEvent>> ReadAsync(CancellationToken ct = default);
}
