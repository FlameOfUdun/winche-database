using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Winche.Database.Constants;
using Winche.Database.AspNetCore.WebSockets.Connections;
using Winche.Database.AspNetCore.WebSockets.Routing;

namespace Winche.Database.AspNetCore.WebSockets.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWincheDatabaseWsApi(
        this IServiceCollection services, Action<WsOptions>? configure = null)
    {
        var options = new WsOptions();
        configure?.Invoke(options);
        services.AddSingleton(Options.Create(options));
        if (options.SettingsEnabled)
        {
            var area = Winche.Core.ServiceCollectionExtensions.AddWincheSettingsArea(services, LiveConnectionSettings.Area, "Live connections",
                LiveConnectionSettings.From(options), s => s.Problems());
            area.Changed += s => s.ApplyTo(options);
        }
        services.AddSingleton<MessageRouter>();
        services.AddSingleton(sp => new WsConnectionRegistry(
            sp.GetRequiredKeyedService<NpgsqlDataSource>(WincheDatabaseKeys.DataSource),
            sp.GetRequiredService<ILogger<WsConnectionRegistry>>()));
        services.AddHostedService(sp => new WsRevocationListener(
            sp.GetRequiredKeyedService<NpgsqlDataSource>(WincheDatabaseKeys.DataSource),
            sp.GetRequiredService<WsConnectionRegistry>(),
            sp.GetRequiredService<ILogger<WsRevocationListener>>()));
        return services;
    }
}
