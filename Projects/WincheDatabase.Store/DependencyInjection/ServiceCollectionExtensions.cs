using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WincheDatabase.Core.Models;
using WincheDatabase.Store.BackgroundServices;
using WincheDatabase.Store.Models;
using WincheDatabase.Store.Interfaces;
using WincheSentinel.Core.DependencyInjection;
using WincheDatabase.Store.Services;
using WincheDatabase.Store.Constants;

namespace WincheDatabase.Store.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWincheDatabaseDocumentStore(this IServiceCollection services, string connectionString, IConfiguration configuration, Action<DependencyConfigurator>? configure = null)
    {
        services.Configure<StoreOptions>(configuration.GetSection(ServiceKeys.CONFIG_SECTION_KEY));

        var contextAccessor = new CallerContextAccessor();

        services.AddWincheSentinel<Document>()
            .AddResourceObjectAccessor<DocumentObjectAccessor>()
            .AddCallerContextAccessor(contextAccessor);

        configure?.Invoke(new DependencyConfigurator(services));

        services.AddNpgsqlDataSource(connectionString, serviceKey: ServiceKeys.DATA_SOURCE_KEY);

        services.AddSingleton(contextAccessor);
        services.AddSingleton<IEventChannel, EventChannel>();
        services.AddSingleton<ISubscriptionRegistry, SubscriptionRegistry>();
        services.AddSingleton<ITransactionRegistry, TransactionRegistry>();
        services.AddSingleton<HookInvocationDispatcher>();
        services.AddSingleton<IDocumentManager, DocumentManager>();
        services.AddSingleton<ISchemaManager, SchemaManager>();
        services.AddSingleton<IChangeProcessor, ChangeProcessor>();
        services.AddSingleton<ISubscriptionManager, SubscriptionManager>();
        services.AddSingleton<ITransactionManager, TransactionManager>();

        services.AddHostedService<ChangeNotifier>();
        services.AddHostedService<TransactionInvalidator>();
        services.AddHostedService<EventNotifier>();
        services.AddHostedService<HookInvocationProcessor>();

        return services;
    }
}