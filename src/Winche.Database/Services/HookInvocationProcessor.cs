using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Winche.Database.Models;

namespace Winche.Database.Services;

public sealed class HookInvocationProcessor(
    HookInvocationDispatcher dispatcher,
    ILogger<HookInvocationProcessor> logger
) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tasks = dispatcher.Readers
            .Select(r => ConsumeAsync(r.Reader, stoppingToken))
            .ToList();

        return tasks.Count == 0 ? Task.CompletedTask : Task.WhenAll(tasks);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        dispatcher.Complete();
        return base.StopAsync(cancellationToken);
    }

    private async Task ConsumeAsync(System.Threading.Channels.ChannelReader<HookInvocation> reader, CancellationToken ct)
    {
        await foreach (var invocation in reader.ReadAllAsync(CancellationToken.None))
        {
            try
            {
                await invocation.Invoke(ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error occurred while invoking hook.");
            }
        }
    }
}
