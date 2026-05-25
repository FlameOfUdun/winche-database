using Microsoft.Extensions.DependencyInjection;
using Winche.Database.AspNetCore.Rest.Abstraction;

namespace Winche.Database.AspNetCore.Rest.DependencyInjection;

public sealed class DependencyConfigurator(IServiceCollection services)
{
    public DependencyConfigurator AddClaimsMapper<TMapper>() where TMapper : RestClaimsMapper
    {
        services.AddSingleton<RestClaimsMapper, TMapper>();
        return this;
    }
}
