using Microsoft.Extensions.DependencyInjection;
using WincheDatabase.Core.Models;
using WincheDatabase.Store.Models;
using WincheSentinel.Core.DependencyInjection;

namespace WincheDatabase.Store.DependencyInjection;

public sealed class DependencyConfigurator(IServiceCollection services)
{
    public DependencyConfigurator AddDocumentAccessRule(DocumentAccessRule rule)
    {
        services.ConfigureWincheSentinel<Document>(configurator =>
        {
            configurator.AddAccessRule(rule);
        });
        return this;
    }
}
