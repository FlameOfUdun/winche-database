using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Winche.Database.AspNetCore.WebSockets.Connections;

/// <summary>Listens for revocations sent by other nodes (see <see cref="WsConnectionRegistry"/>).</summary>
internal sealed class WsRevocationListener(NpgsqlDataSource source, WsConnectionRegistry registry, ILogger<WsRevocationListener> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // A dedicated connection: LISTEN state must not leak into the pool.
                await using var connection = source.CreateConnection();
                await connection.OpenAsync(stoppingToken);
                connection.Notification += (_, e) =>
                {
                    if (e.Channel == WsConnectionRegistry.NotifyChannel)
                        _ = registry.HandleNotificationAsync(e.Payload);
                };
                await using (var listen = connection.CreateCommand())
                {
                    listen.CommandText = $"LISTEN {WsConnectionRegistry.NotifyChannel}";
                    await listen.ExecuteNonQueryAsync(stoppingToken);
                }
                while (true) await connection.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "WebSocket revocation listener lost its connection; reconnecting");
                try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); } catch (OperationCanceledException) { return; }
            }
        }
    }
}
