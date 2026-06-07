using Microsoft.Extensions.DependencyInjection;
using Winche.Database.AspNetCore.Abstraction;
using Winche.Database.Documents;
using Winche.Database.DependencyInjection;
using Winche.Sentinel.DependencyInjection;
using Winche.Sentinel.Interfaces;

namespace Winche.Database.AspNetCore.DependencyInjection;

public static class DependencyConfiguratorExtensions
{
    public static DependencyConfigurator SetCallerClaimsAccessor<TAccessor>(this DependencyConfigurator configurator)
        where TAccessor : DocumentClaimsAccessor
    {
        configurator.Services.ConfigureWincheSentinel<Document>(c => c.SetCallerClaimsAccessor<TAccessor>());
        configurator.Services.AddSingleton(sp => (DocumentClaimsAccessor)sp.GetRequiredService<ICallerClaimsAccessor<Document>>());
        return configurator;
    }
}
