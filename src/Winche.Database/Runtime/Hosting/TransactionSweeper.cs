using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Winche.Database.DependencyInjection;

namespace Winche.Database.Runtime.Hosting;

/// <summary>Sweeps expired ledger entries (spec §3). Correctness never depends on it.</summary>
public sealed class TransactionSweeper(
    DocumentDatabase core,
    IOptions<WincheDatabaseOptions> options,
    ILogger<TransactionSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = options.Value.TransactionConfig;
        // In the database, expiry is checked on every access and the sweep only tidies the table: no need to
        // run it every second on every node.
        var interval = config.Store == TransactionStore.Database && config.CleanupInterval < TimeSpan.FromMinutes(1)
            ? TimeSpan.FromMinutes(1)
            : config.CleanupInterval;
        using var timer = new PeriodicTimer(interval);
        while (true)
        {
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) break; }
            catch (OperationCanceledException) { break; }

            int swept;
            try { swept = await core.Ledger.RemoveExpiredAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Sweeping expired transactions failed");
                continue;
            }
            if (swept > 0)
                logger.LogDebug("Swept {Count} expired transactions", swept);
        }
    }
}
