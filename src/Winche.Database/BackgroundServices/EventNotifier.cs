using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Winche.Database.Interfaces;

namespace Winche.Database.BackgroundServices;

public sealed class EventNotifier(
    IEventChannel channel,
    IEnumerable<ISubscriptionEventHandler> handlers,
    ILogger<EventNotifier> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var events in channel.ReadAsync(stoppingToken))
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
