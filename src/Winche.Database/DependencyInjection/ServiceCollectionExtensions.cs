using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Winche.Database.Documents;
using Winche.Database.Services;
using Winche.Database.Interfaces;
using Winche.Database.BackgroundServices;
using Winche.Database.Constants;
using Winche.Database.Models;
using Winche.Sentinel.DependencyInjection;

namespace Winche.Database.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWincheDatabase(this IServiceCollection services, IConfiguration configuration, Action<DependencyConfigurator>? configure = null)
    {
        var connectionString =
            configuration.GetConnectionString(ServiceKeys.CONN_STRING_KEY) ??
            configuration.GetConnectionString("DefaultConnection") ??
            throw new InvalidOperationException(
                $"Connection string '{ServiceKeys.CONN_STRING_KEY}' or fallback 'DefaultConnection' is not configured.");

        services.AddWincheSentinel<Document>();
        services.Configure<StoreOptions>(configuration.GetSection(ServiceKeys.CONFIG_SECTION_KEY));
        configure?.Invoke(new DependencyConfigurator(services));

        services.AddNpgsqlDataSource(connectionString, serviceKey: ServiceKeys.DATA_SOURCE_KEY);

        services.AddSingleton<IEventChannel, EventChannel>();
        services.AddSingleton<ISubscriptionRegistry, SubscriptionRegistry>();
        services.AddSingleton<ITransactionRegistry, TransactionRegistry>();
        services.AddSingleton<IDocumentManager, DocumentManager>();
        services.AddSingleton<ISchemaManager, SchemaManager>();
        services.AddSingleton<IChangeProcessor, ChangeProcessor>();
        services.AddSingleton<ISubscriptionManager, SubscriptionManager>();
        services.AddSingleton<ITransactionManager, TransactionManager>();
        services.AddSingleton<HookInvocationDispatcher>();

        services.AddHostedService<ChangeNotifier>();
        services.AddHostedService<TransactionInvalidator>();
        services.AddHostedService<EventNotifier>();
        services.AddHostedService<HookInvocationProcessor>();

        return services;
    }
}
