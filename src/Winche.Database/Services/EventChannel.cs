using System.Threading.Channels;
using Winche.Database.Interfaces;
using Winche.Database.Models;

namespace Winche.Database.Services;

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
