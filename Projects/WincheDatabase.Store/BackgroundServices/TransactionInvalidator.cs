using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WincheDatabase.Store.Models;
using WincheDatabase.Store.Interfaces;

namespace WincheDatabase.Store.BackgroundServices;

public sealed class TransactionInvalidator(
    IOptions<StoreOptions> options,
    ITransactionRegistry registry,
    ILogger<TransactionInvalidator> logger
) : BackgroundService
{
    private readonly TransactionConfig _config = options.Value.TransactionConfig;
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_config.CleanupInterval, ct);
                await RunCleanupAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error during transaction cleanup");
            }
        }

        logger.LogInformation("Transaction cleanup service stopped");
    }

    private async Task RunCleanupAsync(CancellationToken ct)
    {
        var before = registry.Count;

        await registry.RemoveExpiredAsync(_config.TotalTimeoutSpan, ct);

        var after = registry.Count;
        var cleaned = before - after;

        if (cleaned > 0)
        {
            logger.LogWarning("Cleaned up {Count} expired or completed transactions. Active: {Active}", cleaned, after);
        }
    }
}
