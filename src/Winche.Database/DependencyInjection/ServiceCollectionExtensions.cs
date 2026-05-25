using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WincheSentinel.DependencyInjection;
using Winche.Database.Services;
using Winche.Database.Interfaces;
using Winche.Database.BackgroundServices;
using Winche.Database.Core.Models;
using Winche.Database.Constants;
using Winche.Database.Models;

namespace Winche.Database.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWincheDatabase(this IServiceCollection services, string connectionString, IConfiguration configuration, Action<DependencyConfigurator>? configure = null)
    {
        services.Configure<StoreOptions>(configuration.GetSection(ServiceKeys.CONFIG_SECTION_KEY));

        var contextAccessor = new CallerContextAccessor();

        services.AddWincheSentinel<Document>(c =>
        {
            c.AddResourceObjectAccessor<DocumentObjectAccessor>();
            c.AddCallerContextAccessor(contextAccessor);
        });

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