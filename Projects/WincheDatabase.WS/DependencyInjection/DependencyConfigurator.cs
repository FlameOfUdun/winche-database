using Microsoft.Extensions.DependencyInjection;
using WincheDatabase.WS.Services;

namespace WincheDatabase.Ws.DependencyInjection;

public sealed class DependencyConfigurator(IServiceCollection services)
{
    public DependencyConfigurator AddClaimsMapper(WsClaimsMapper instance)
    {
        services.AddSingleton(instance);
        return this;
    }
}
