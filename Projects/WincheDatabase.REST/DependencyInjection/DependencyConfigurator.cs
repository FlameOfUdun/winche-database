using Microsoft.Extensions.DependencyInjection;
using WincheDatabase.REST.Abstraction;

namespace WincheDatabase.REST.DependencyInjection;

public sealed class DependencyConfigurator(IServiceCollection services)
{
    public DependencyConfigurator AddClaimsMapper<TMapper>() where TMapper : RestClaimsMapper
    {
        services.AddSingleton<RestClaimsMapper, TMapper>();
        return this;
    }
}
