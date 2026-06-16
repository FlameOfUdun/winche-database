using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Winche.Database.Abstraction;
using Winche.Database.Authorization;
using Winche.Database.Constants;
using Winche.Database.Querying;
using Winche.Database.Runtime;
using Winche.Database.Runtime.ChangeFeed;
using Winche.Database.Runtime.Hosting;
using Winche.Database.Runtime.Listening;
using Winche.Database.Runtime.Writes;
using Winche.Database.Schema;
using Winche.Rules;
using Winche.Rules.DependencyInjection;

namespace Winche.Database.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWincheDatabase(
        this IServiceCollection services, Action<WincheDatabaseOptions> configure)
    {
        // Register the claims default BEFORE calling configure, so a user MapClaims() call inside
        // configure registers AFTER it and wins (.NET DI's GetRequiredService<T>() returns the
        // last-registered singleton). The default deny-all ruleset is seeded via AddWincheRules below.
        services.AddSingleton<IRuleClaimsAccessor>(NullRuleClaimsAccessor.Instance);  // default null claims

        var options = new WincheDatabaseOptions(services);
        configure(options);

        var connectionString = !string.IsNullOrWhiteSpace(options.ConnectionString)
            ? options.ConnectionString
            : throw new InvalidOperationException(
                $"{nameof(WincheDatabaseOptions)}.{nameof(WincheDatabaseOptions.ConnectionString)} is required.");

        services.AddSingleton(Options.Create(options));

        services.AddNpgsqlDataSource(connectionString, serviceKey: ServiceKeys.DATA_SOURCE_KEY);

        services.AddSingleton<CollectionIndexResolver>();
        services.AddSingleton(sp => new ListenerRegistry(
            sp.GetRequiredKeyedService<NpgsqlDataSource>(ServiceKeys.DATA_SOURCE_KEY),
            sp.GetRequiredService<CollectionIndexResolver>())
        );
        services.AddSingleton(sp => new DocumentDatabase(
            sp.GetRequiredKeyedService<NpgsqlDataSource>(ServiceKeys.DATA_SOURCE_KEY),
            sp.GetRequiredService<IOptions<WincheDatabaseOptions>>(),
            sp.GetRequiredService<ListenerRegistry>(),
            sp.GetRequiredService<CollectionIndexResolver>()
        ));

        services.AddSingleton<ISchemaManager, SchemaManager>();

        // Change feed consumers
        services.AddSingleton<IChangeFeedConsumer>(sp => sp.GetRequiredService<ListenerRegistry>());
        services.AddSingleton<IChangeFeedConsumer>(sp => new HookFeedConsumer(
            sp.GetRequiredService<IEnumerable<HookRegistration>>()));

        services.AddHostedService<ChangeFeedHostedService>();
        services.AddHostedService<RetentionPruner>();
        services.AddHostedService<TransactionSweeper>();

        // ── Winche.Rules guard ─────────────────────────────────────────────────
        // Register the engine via Winche.Rules DI. It merges every RuleSet registered by UseRules()
        // (plus the default deny-all seed below) and uses the database's engine-faithful comparer.
        services.AddWincheRules(o => o
            .WithComparer(WincheRuleValueComparer.Instance)
            .WithRuleset(_ => { })
        );                                     // default deny-all seed

        services.AddSingleton<IWriteAuthorizer>(sp => new RulesWriteAuthorizer(
            sp.GetRequiredService<RuleEngine>(),
            sp.GetRequiredService<IRuleClaimsAccessor>())
        );

        // Plain DocumentDatabase (above) stays the unguarded bypass core. The read-guard wraps a
        // separate authorizing core (writes go through IWriteAuthorizer inside the transaction).
        services.AddSingleton(sp =>
            new RuleGuardedDocumentDatabase(
                new DocumentDatabase(
                    sp.GetRequiredKeyedService<NpgsqlDataSource>(ServiceKeys.DATA_SOURCE_KEY),
                    sp.GetRequiredService<IOptions<WincheDatabaseOptions>>(),
                    sp.GetRequiredService<ListenerRegistry>(),
                    sp.GetRequiredService<CollectionIndexResolver>(),
                    sp.GetRequiredService<IWriteAuthorizer>()
                ),
                sp.GetRequiredService<RuleEngine>(),
                sp.GetRequiredService<IRuleClaimsAccessor>())
            );

        services.AddSingleton<IDocumentDatabase>(sp =>
            sp.GetRequiredService<RuleGuardedDocumentDatabase>()
        );

        return services;
    }
}
