using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Winche.Database.Constants;
using Winche.Database.DependencyInjection;
using Winche.Database.Runtime.ChangeFeed;

namespace Winche.Database.Runtime.Hosting;

public sealed class ChangeFeedHostedService(
    [FromKeyedServices(WincheDatabaseKeys.DataSource)] NpgsqlDataSource source,
    IOptions<WincheDatabaseOptions> options,
    ChangeFeedConsumerSet consumers,
    ILogger<ChangeFeedPump> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await SplitLegacyHookCursorAsync(stoppingToken)) return;
        await new ChangeFeedPump(source, consumers.All, options.Value.ChangeFeed, logger).RunAsync(stoppingToken);
    }

    /// <summary>
    /// Before 10.4.0 every hook shared the "hooks" cursor. On the first start after the upgrade each hook
    /// type's new cursor starts from it, so nothing is skipped or replayed. This must happen before the
    /// runners start (a runner without a cursor starts from now), so it is retried until it succeeds.
    /// </summary>
    private async Task<bool> SplitLegacyHookCursorAsync(CancellationToken ct)
    {
        var names = consumers.HookNames;
        if (names.Count == 0) return true;
        var reader = new ChangeFeedReader(source);
        while (true)
        {
            try
            {
                var created = await reader.InheritCursorAsync(HookFeedConsumer.LegacyDurableName, HookFeedConsumer.DurableNamePrefix, names, ct);
                if (created > 0)
                    logger.LogInformation("Split the shared hooks cursor into {Count} per-hook-type cursors", created);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not set up the hook cursors; retrying");
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { return false; }
            }
        }
    }
}
