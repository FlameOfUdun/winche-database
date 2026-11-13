using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Winche.Database.Constants;
using Winche.Core;
using Winche.Database.Schema;

namespace Winche.Database.DependencyInjection;

public static class HostExtensions
{
    /// <summary>
    /// Creates the winche_* tables/functions (idempotent) and syncs index definitions. Nodes starting
    /// together take turns (a Postgres advisory lock): concurrent DDL on the same objects fails with
    /// "tuple concurrently updated" and can take conflicting locks.
    /// </summary>
    public static async Task InitializeWincheDatabaseAsync(this IHost host, CancellationToken ct = default)
    {
        using var scope = host.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredKeyedService<NpgsqlDataSource>(WincheDatabaseKeys.DataSource);
        await using var _ = await AdvisoryLease.AcquireAsync(source, "schema", TimeSpan.FromSeconds(30), ct);

        // Saved settings first, so the console's indexes are synced rather than dropped and rebuilt.
        if (scope.ServiceProvider.GetService<Winche.Core.SettingsArea<Settings.DatabaseSettings>>() is not null
            && scope.ServiceProvider.GetService<Winche.Core.SettingsStore>() is { } settings)
            await settings.EnsureLoadedAsync(ct);
        var schema = scope.ServiceProvider.GetRequiredService<ISchemaManager>();
        await schema.EnsureCreatedAsync(ct);
        await schema.SyncIndexesAsync(ct);
    }
}
