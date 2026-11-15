using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Winche.Core;
using Winche.Database.Constants;

namespace Winche.Database.Schema;

/// <summary>
/// The database's setup, step by step: its tables and functions, the change feed's bookkeeping, then the indexes. What
/// <c>InitializeWincheDatabaseAsync</c> does in one go, for hosts that set products up on demand.
/// </summary>
internal sealed class DatabaseProductSetup(IServiceProvider services) : IWincheProductSetup
{
    public string Product => WincheProductNames.Database;

    public IReadOnlyList<string> Steps { get; } = ["Create the document tables and functions", "Prepare the change feed", "Build the indexes"];

    public async Task RunStepAsync(int step, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredKeyedService<NpgsqlDataSource>(WincheDatabaseKeys.DataSource);
        // One node at a time (concurrent DDL on the same objects fails).
        await using var _ = await AdvisoryLease.AcquireAsync(source, "schema", TimeSpan.FromSeconds(30), ct);
        var schema = scope.ServiceProvider.GetRequiredService<ISchemaManager>();
        switch (step)
        {
            case 0:
                await schema.EnsureCreatedAsync(ct);
                break;
            case 1:
                await Winche.Core.Feeds.WincheFeedTables.EnsureAsync(source, ct);
                break;
            case 2:
                // Saved settings first, so the console's indexes are built rather than dropped.
                if (scope.ServiceProvider.GetService<SettingsArea<Settings.DatabaseSettings>>() is not null
                    && scope.ServiceProvider.GetService<SettingsStore>() is { } settings)
                    await settings.EnsureLoadedAsync(ct);
                await schema.SyncIndexesAsync(ct);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(step));
        }
    }
}
