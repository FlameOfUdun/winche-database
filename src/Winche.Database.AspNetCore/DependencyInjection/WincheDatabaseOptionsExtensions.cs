using Microsoft.Extensions.DependencyInjection;
using Winche.Database.AspNetCore.Abstraction;
using Winche.Database.DependencyInjection;
using Winche.Database.Documents;
using Winche.Sentinel.DependencyInjection;
using Winche.Sentinel.Interfaces;

namespace Winche.Database.AspNetCore.DependencyInjection;

public static class WincheDatabaseOptionsExtensions
{
    public static WincheDatabaseOptions SetCallerClaimsAccessor<TAccessor>(this WincheDatabaseOptions options)
        where TAccessor : DocumentClaimsAccessor
    {
        options.Services.ConfigureWincheSentinel<Document>(c => c.SetCallerClaimsAccessor<TAccessor>());
        options.Services.AddSingleton(sp => (DocumentClaimsAccessor)sp.GetRequiredService<ICallerClaimsAccessor<Document>>());
        return options;
    }
}
