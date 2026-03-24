using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WincheDb.DocumentStore.Services;

namespace WincheDb.DocumentStore.DependencyInjection
{
    public static class HostExtensions
    {
        public static async Task<IHost> UseWincheDbStore(this IHost host)
        {
            using var scope = host.Services.CreateScope();
            var service = scope.ServiceProvider.GetService<SchemaManager>()!;
            await service.EnsureCreatedAsync();

            return host;
        }
    }
}
