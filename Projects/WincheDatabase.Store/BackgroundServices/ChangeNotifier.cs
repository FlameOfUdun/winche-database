using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using WincheDatabase.Store.Models;
using WincheDatabase.Store.Services;

namespace WincheDatabase.Store.BackgroundServices;

public class ChangeNotifier(
    NpgsqlDataSource source,
    ChangeProcessor changeProcessor,
    ILogger<ChangeNotifier> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ListenForChangesAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in document change listener, reconnecting...");
                await Task.Delay(1000, ct);
            }
        }
    }

    private async Task ListenForChangesAsync(CancellationToken ct)
    {
        await using var conn = await source.OpenConnectionAsync(ct);

        var pending = new ConcurrentQueue<DocumentChange>();

        conn.Notification += (_, e) =>
        {
            try
            {
                var change = JsonSerializer.Deserialize<DocumentChange>(e.Payload);
                if (change == null)
                {
                    logger.LogWarning("Failed to deserialize change payload: {Payload}", e.Payload);
                    return;
                }

                pending.Enqueue(change);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error deserializing notification: {Payload}", e.Payload);
            }
        };

        await using (var cmd = new NpgsqlCommand("LISTEN document_changes", conn))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        logger.LogInformation("Listening for document changes");

        while (!ct.IsCancellationRequested)
        {
            await conn.WaitAsync(ct);

            while (pending.TryDequeue(out var change))
            {
                try
                {
                    await changeProcessor.ProcessAsync(change, ct);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error processing change for {Collection}/{Id}", change.Collection, change.Id);
                }
            }
        }
    }
}
