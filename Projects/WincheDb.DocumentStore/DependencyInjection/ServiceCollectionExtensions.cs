using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using WincheDb.DocumentStore.BackgroundServices;
using WincheDb.DocumentStore.Models;
using WincheDb.DocumentStore.Services;
using WincheDb.DocumentStore.Stores;

namespace WincheDb.DocumentStore.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWincheDbStore(this IServiceCollection services)
    {
        return AddWincheDbStore(services, options => options);
    }

    public static IServiceCollection AddWincheDbStore(this IServiceCollection services, Func<StoreOptions, StoreOptions> builder)
    {
        // Options
        var options = builder(new StoreOptions());
        services.AddSingleton(options);

        // Channel for subscription events.
        var channel = Channel.CreateBounded<List<SubscriptionEvent>>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        services.AddSingleton(channel.Reader);
        services.AddSingleton(channel.Writer);

        // Stores
        services.AddSingleton<SubscriptionRegistry>();
        services.AddSingleton<TransactionRegistry>();

        // Services
        services.AddSingleton<DocumentManager>();
        services.AddSingleton<SchemaManager>();
        services.AddSingleton<ChangeProcessor>();
        services.AddSingleton<SubscriptionManager>();
        services.AddSingleton<TransactionManager>();

        // Workers
        services.AddHostedService<ChangeNotifier>();
        services.AddHostedService<TransactionInvalidator>();
        services.AddHostedService<EventNotifier>();

        return services;
    }
}