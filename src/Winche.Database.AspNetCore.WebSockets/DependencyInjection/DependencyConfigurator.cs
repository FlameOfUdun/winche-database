using Microsoft.Extensions.DependencyInjection;
using Winche.Database.AspNetCore.WebSockets.Abstraction;

namespace Winche.Database.AspNetCore.WebSockets.DependencyInjection;

public sealed class DependencyConfigurator(IServiceCollection services)
{
    public DependencyConfigurator AddClaimsMapper<TMapper>() where TMapper : WsClaimsMapper
    {
        services.AddSingleton<WsClaimsMapper, TMapper>();
        return this;
    }
}
