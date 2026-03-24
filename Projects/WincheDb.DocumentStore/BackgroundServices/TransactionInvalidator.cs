using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WincheDb.DocumentStore.Stores;

namespace WincheDb.DocumentStore.BackgroundServices;

public sealed class TransactionInvalidator(
    StoreOptions options,
    TransactionRegistry registry,
    ILogger<TransactionInvalidator> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(options.TransactionConfig.CleanupInterval, ct);
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

        await registry.RemoveExpiredAsync(options.TransactionConfig.TotalTimeoutSpan, ct);

        var after = registry.Count;
        var cleaned = before - after;

        if (cleaned > 0)
        {
            logger.LogWarning("Cleaned up {Count} expired or completed transactions. Active: {Active}", cleaned, after);
        }
    }
}
