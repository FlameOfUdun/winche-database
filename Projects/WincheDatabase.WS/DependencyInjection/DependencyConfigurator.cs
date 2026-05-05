using Microsoft.Extensions.DependencyInjection;
using WincheDatabase.WS.Abstraction;

namespace WincheDatabase.Ws.DependencyInjection;

public sealed class DependencyConfigurator(IServiceCollection services)
{
    public DependencyConfigurator AddClaimsMapper<TMapper>() where TMapper : WsClaimsMapper
    {
        services.AddSingleton<WsClaimsMapper, TMapper>();
        return this;
    }
}
