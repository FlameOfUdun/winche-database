using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Winche.Database.Constants;
using Winche.Database.DependencyInjection;
using Winche.Database.Runtime.ChangeFeed;

namespace Winche.Database.Runtime.Hosting;

/// <summary>
/// Bounds the feed table: deletes rows older than ChangeFeed.Retention once every durable consumer has
/// delivered them, and rows older than ChangeFeed.MaxRetention regardless (logged as an error, since a
/// consumer then misses them).
/// </summary>
public sealed class RetentionPruner(
    [FromKeyedServices(WincheDatabaseKeys.DataSource)] NpgsqlDataSource source,
    IOptions<WincheDatabaseOptions> options,
    ChangeFeedConsumerSet consumers,
    ILogger<RetentionPruner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = new ChangeFeedReader(source);
        var config = options.Value.ChangeFeed;
        using var timer = new PeriodicTimer(config.PruneInterval);
        while (await Wait(timer, stoppingToken))
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var (pruned, undelivered) = await reader.PruneAsync(
                    now - config.Retention, now - config.MaxRetention, consumers.DurableNames, stoppingToken);
                if (undelivered > 0)
                    logger.LogError(
                        "Pruned {Count} change-feed rows older than {MaxRetention} that a durable consumer had not delivered; " +
                        "it will never see them (check ChangeFeedConsumers.GetStatusAsync for the consumer that is stuck)",
                        undelivered, config.MaxRetention);
                if (pruned > 0)
                    logger.LogInformation("Pruned {Count} change-feed rows", pruned);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Change-feed pruning failed");
            }
        }
    }

    private static async Task<bool> Wait(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
