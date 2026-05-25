using Microsoft.Extensions.DependencyInjection;

namespace Winche.Database.AspNetCore.Rest.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWincheDatabaseRestApi(this IServiceCollection services, Action<DependencyConfigurator>? configure = null)
    {
        var configurator = new DependencyConfigurator(services);
        configure?.Invoke(configurator);

        return services;
    }
}