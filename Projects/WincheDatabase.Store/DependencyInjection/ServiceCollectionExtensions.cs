using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Channels;
using WincheDatabase.Store;
using WincheDatabase.Store.BackgroundServices;
using WincheDatabase.Store.Models;
using WincheDatabase.Store.Services;
using WincheDatabase.Store.Stores;

namespace WincheDatabase.Store.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWincheDatabaseDocumentStore(this IServiceCollection services, string connectionString, IConfiguration configuration, Action<List<AccessRule>>? configureRules = null)
    {
        var section = configuration.GetSection("WincheDatabase");

        services.Configure<StoreOptions>(section);

        services.PostConfigure<StoreOptions>(options =>
        {
            options.AccessRules = [];
            configureRules?.Invoke(options.AccessRules);
        });

        services.AddNpgsqlDataSource(connectionString);

        var channel = Channel.CreateBounded<List<SubscriptionEvent>>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        services.AddSingleton(channel.Reader);
        services.AddSingleton(channel.Writer);

        services.AddSingleton<SubscriptionRegistry>();
        services.AddSingleton<TransactionRegistry>();

        services.AddSingleton<DocumentManager>();
        services.AddSingleton<SchemaManager>();
        services.AddSingleton<ChangeProcessor>();
        services.AddSingleton<SubscriptionManager>();
        services.AddSingleton<TransactionManager>();
        services.AddSingleton<AccessRuleEvaluator>();

        services.AddHostedService<ChangeNotifier>();
        services.AddHostedService<TransactionInvalidator>();
        services.AddHostedService<EventNotifier>();

        return services;
    }
}