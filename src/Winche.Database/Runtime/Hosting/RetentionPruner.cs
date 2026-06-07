using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Winche.Database.Constants;
using Winche.Database.Models;
using Winche.Database.Runtime.ChangeFeed;

namespace Winche.Database.Runtime.Hosting;

/// <summary>Bounds the feed table: deletes rows older than ChangeFeed.Retention (spec §4).</summary>
public sealed class RetentionPruner(
    [FromKeyedServices(ServiceKeys.DATA_SOURCE_KEY)] NpgsqlDataSource source,
    IOptions<StoreOptions> options,
    ILogger<RetentionPruner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = new ChangeFeedReader(source, options.Value.TableName);
        var config = options.Value.ChangeFeed;
        using var timer = new PeriodicTimer(config.PruneInterval);
        while (await Wait(timer, stoppingToken))
        {
            try
            {
                var pruned = await reader.PruneBeforeAsync(DateTimeOffset.UtcNow - config.Retention, stoppingToken);
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
