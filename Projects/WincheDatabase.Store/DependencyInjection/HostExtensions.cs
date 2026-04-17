using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WincheDatabase.Store.Services;

namespace WincheDatabase.Store.DependencyInjection
{
    public static class HostExtensions
    {
        public static IHost UseWincheDatabaseDocumentStore(this IHost host)
        {
            using var scope = host.Services.CreateScope();
            var service = scope.ServiceProvider.GetService<SchemaManager>()!;
            service.EnsureCreatedAsync().GetAwaiter().GetResult();

            return host;
        }
    }
}
