using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Winche.Database.Abstraction;
using Winche.Database.Constants;
using Winche.Database.Documents;
using Winche.Database.DependencyInjection;
using Winche.Database.Runtime;
using Winche.Database.Runtime.ChangeFeed;
using Winche.Database.Runtime.Hosting;
using Winche.Database.Runtime.Listening;
using Winche.Database.Schema;
using Winche.Sentinel.DependencyInjection;
using Winche.Sentinel.Interfaces;

namespace Winche.Database.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWincheDatabase(
        this IServiceCollection services, Action<WincheDatabaseOptions> configure)
    {
        services.AddWincheSentinel<Document>();

        var options = new WincheDatabaseOptions(services);
        configure(options);

        var connectionString = !string.IsNullOrWhiteSpace(options.ConnectionString)
            ? options.ConnectionString
            : throw new InvalidOperationException(
                $"{nameof(WincheDatabaseOptions)}.{nameof(WincheDatabaseOptions.ConnectionString)} is required.");

        services.AddSingleton<IOptions<WincheDatabaseOptions>>(Options.Create(options));

        services.AddNpgsqlDataSource(connectionString, serviceKey: ServiceKeys.DATA_SOURCE_KEY);

        // Core runtime (spec architecture): rule-free core + guard as the public surface
        services.AddSingleton<ListenerRegistry>(sp => new ListenerRegistry(
            sp.GetRequiredKeyedService<NpgsqlDataSource>(ServiceKeys.DATA_SOURCE_KEY)));
        services.AddSingleton<DocumentDatabase>(sp => new DocumentDatabase(
            sp.GetRequiredKeyedService<NpgsqlDataSource>(ServiceKeys.DATA_SOURCE_KEY),
            sp.GetRequiredService<IOptions<WincheDatabaseOptions>>(),
            sp.GetRequiredService<ListenerRegistry>()));
        services.AddSingleton<IDocumentDatabase>(sp => new GuardedDocumentDatabase(
            sp.GetRequiredService<DocumentDatabase>(),
            sp.GetRequiredService<IAccessRuleEvaluator<Document>>()));

        services.AddSingleton<ISchemaManager, SchemaManager>();

        // Change feed consumers
        services.AddSingleton<IChangeFeedConsumer>(sp => sp.GetRequiredService<ListenerRegistry>());
        services.AddSingleton<IChangeFeedConsumer>(sp => new HookFeedConsumer(
            sp.GetRequiredService<IEnumerable<DocumentStoreHook>>(),
            sp.GetRequiredService<IPathPatternMatcher<Document>>()));

        services.AddHostedService<ChangeFeedHostedService>();
        services.AddHostedService<RetentionPruner>();
        services.AddHostedService<TransactionSweeper>();

        return services;
    }
}
