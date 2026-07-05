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
    IEnumerable<IChangeFeedConsumer> consumers,
    ILogger<ChangeFeedPump> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        new ChangeFeedPump(source, [.. consumers],
            options.Value.ChangeFeed, logger).RunAsync(stoppingToken);
}
