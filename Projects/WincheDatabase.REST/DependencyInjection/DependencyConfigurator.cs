using Microsoft.Extensions.DependencyInjection;
using WincheDatabase.REST.Services;

namespace WincheDatabase.REST.DependencyInjection;

public sealed class DependencyConfigurator(IServiceCollection services)
{
    public DependencyConfigurator AddClaimsMapper(RestClaimsMapper instance)
    {
        services.AddSingleton(instance);
        return this;
    }
}
