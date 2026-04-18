using WincheDatabase.Store.Models;

namespace WincheDatabase.Store.Abstraction;

public interface IEventChannel
{
    Task WriteAsync(List<SubscriptionEvent> events, CancellationToken ct = default);
    IAsyncEnumerable<List<SubscriptionEvent>> ReadAsync(CancellationToken ct = default);
}
