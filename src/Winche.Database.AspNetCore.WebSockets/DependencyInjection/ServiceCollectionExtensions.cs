using Microsoft.Extensions.DependencyInjection;
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
        services.AddSingleton(options);
        services.AddSingleton<MessageRouter>();
        return services;
    }
}
