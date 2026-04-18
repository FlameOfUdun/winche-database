using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WincheDatabase.Store.Abstraction;

namespace WincheDatabase.Store.DependencyInjection
{
    public static class HostExtensions
    {
        public static IHost UseWincheDatabaseDocumentStore(this IHost host)
        {
            using var scope = host.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<ISchemaManager>()!;
            service.EnsureCreatedAsync().GetAwaiter().GetResult();

            return host;
        }
    }
}
