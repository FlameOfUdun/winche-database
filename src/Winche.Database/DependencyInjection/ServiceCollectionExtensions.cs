using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Winche.Database.Constants;
using Winche.Database.Documents;
using Winche.Database.Interfaces;
using Winche.Database.Models;
using Winche.Database.Runtime;
using Winche.Database.Runtime.ChangeFeed;
using Winche.Database.Runtime.Hosting;
using Winche.Database.Runtime.Listening;
using Winche.Database.Services;
using Winche.Sentinel.DependencyInjection;
using Winche.Sentinel.Interfaces;

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

        // Core runtime (spec architecture): rule-free core + guard as the public surface
        services.AddSingleton<ListenerRegistry>(sp => new ListenerRegistry(
            sp.GetRequiredKeyedService<NpgsqlDataSource>(ServiceKeys.DATA_SOURCE_KEY),
            sp.GetRequiredService<IOptions<StoreOptions>>()));
        services.AddSingleton<DocumentDatabase>(sp => new DocumentDatabase(
            sp.GetRequiredKeyedService<NpgsqlDataSource>(ServiceKeys.DATA_SOURCE_KEY),
            sp.GetRequiredService<IOptions<StoreOptions>>(),
            sp.GetRequiredService<ListenerRegistry>()));
        services.AddSingleton<IDocumentDatabase>(sp => new GuardedDocumentDatabase(
            sp.GetRequiredService<DocumentDatabase>(),
            sp.GetRequiredService<IAccessRuleEvaluator<Document>>()));

        services.AddSingleton<ISchemaManager, SchemaManager>();
        services.AddSingleton<HookInvocationDispatcher>();
        services.AddSingleton<IHookInvocationDispatcher>(sp => sp.GetRequiredService<HookInvocationDispatcher>());

        // Change feed consumers
        services.AddSingleton<IChangeFeedConsumer>(sp => sp.GetRequiredService<ListenerRegistry>());
        services.AddSingleton<IChangeFeedConsumer>(sp => new HookFeedConsumer(
            sp.GetRequiredService<HookInvocationDispatcher>()));

        services.AddHostedService<ChangeFeedHostedService>();
        services.AddHostedService<RetentionPruner>();
        services.AddHostedService<TransactionSweeper>();
        services.AddHostedService<HookInvocationProcessor>();

        return services;
    }
}
