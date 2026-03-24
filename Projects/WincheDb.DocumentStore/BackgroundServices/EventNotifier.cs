using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WincheDb.DocumentStore.Abstraction;
using WincheDb.DocumentStore.Models;

namespace WincheDb.DocumentStore.BackgroundServices;

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
                    logger.LogError(ex, "Handler {Handler} failed for {Count} events",
                        handler.GetType().Name, events.Count);
                }
            }
        }
    }
}
