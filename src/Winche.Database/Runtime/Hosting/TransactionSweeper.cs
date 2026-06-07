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
        using var timer = new PeriodicTimer(options.Value.TransactionConfig.CleanupInterval);
        while (true)
        {
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) break; }
            catch (OperationCanceledException) { break; }

            var swept = core.Ledger.RemoveExpired();
            if (swept > 0)
                logger.LogDebug("Swept {Count} expired transactions", swept);
        }
    }
}
