using System.Threading.Channels;
using WincheDatabase.Store.Abstraction;
using WincheDatabase.Store.Models;

namespace WincheDatabase.Store.Services;

public class EventChannel : IEventChannel
{
    private readonly Channel<List<SubscriptionEvent>> channel = Channel.CreateBounded<List<SubscriptionEvent>>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
    });

    public async Task WriteAsync(List<SubscriptionEvent> events, CancellationToken ct = default)
    {
        await channel.Writer.WriteAsync(events, ct);
    }

    public IAsyncEnumerable<List<SubscriptionEvent>> ReadAsync(CancellationToken ct = default)
    {
        return channel.Reader.ReadAllAsync(ct);
    }
}
