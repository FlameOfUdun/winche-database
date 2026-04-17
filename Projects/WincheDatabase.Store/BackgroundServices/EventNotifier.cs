using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WincheDatabase.Store.Abstraction;
using WincheDatabase.Store.Models;

namespace WincheDatabase.Store.BackgroundServices;

public sealed class EventNotifier(
    ChannelReader<List<SubscriptionEvent>> eventReader,
    IEnumerable<ISubscriptionEventHandler> handlers,
    ILogger<EventNotifier> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var events in eventReader.ReadAllAsync(stoppingToken))
        {
            foreach (var handler in handlers)
            {
                try
                {
                    await handler.HandleAsync(events, stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Handler {Handler} failed for {Count} events", handler.GetType().Name, events.Count);
                }
            }
        }
    }
}
