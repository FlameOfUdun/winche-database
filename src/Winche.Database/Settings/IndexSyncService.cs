using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Winche.Database.Constants;
using Winche.Database.Runtime.Coordination;
using Winche.Database.Schema;

namespace Winche.Database.Settings;

/// <summary>
/// Builds and drops indexes when the console changes them (CREATE INDEX CONCURRENTLY: writes carry on). One server
/// at a time, under the same lease as startup's sync; the others find the work done.
/// </summary>
internal sealed class IndexSyncService(IndexCatalog catalog, ISchemaManager schema,
    [FromKeyedServices(WincheDatabaseKeys.DataSource)] NpgsqlDataSource source, ILogger<IndexSyncService> logger) : BackgroundService
{
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public override Task StartAsync(CancellationToken ct)
    {
        catalog.Changed += Signal;
        return base.StartAsync(ct);
    }

    private void Signal() => _changes.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var _ in _changes.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var lease = await AdvisoryLease.AcquireAsync(source, "schema", TimeSpan.FromSeconds(30), stoppingToken);
                await schema.SyncIndexesAsync(stoppingToken);
                logger.LogInformation("Indexes are in step with the settings ({Count} declared).", catalog.Indexes.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Couldn't bring the indexes in step with the settings.");
            }
        }
    }

    public override Task StopAsync(CancellationToken ct)
    {
        catalog.Changed -= Signal;
        return base.StopAsync(ct);
    }
}
