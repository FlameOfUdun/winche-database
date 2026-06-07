using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Winche.Database.Schema;

namespace Winche.Database.DependencyInjection;

public static class HostExtensions
{
    /// <summary>Creates the winche_* tables/functions (idempotent) and syncs index definitions.</summary>
    public static async Task InitializeWincheDatabaseAsync(this IHost host, CancellationToken ct = default)
    {
        using var scope = host.Services.CreateScope();
        var schema = scope.ServiceProvider.GetRequiredService<ISchemaManager>();
        await schema.EnsureCreatedAsync(ct);
        await schema.SyncIndexesAsync(ct);
    }
}
