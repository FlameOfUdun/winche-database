using WincheDatabase.Store.Models;

namespace WincheDatabase.Store.Interfaces;

public interface IEventChannel
{
    Task WriteAsync(List<SubscriptionEvent> events, CancellationToken ct = default);
    IAsyncEnumerable<List<SubscriptionEvent>> ReadAsync(CancellationToken ct = default);
}
