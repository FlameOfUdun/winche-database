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
        // last-registered singleton). The keyed rules engine is built from options.Rulesets below.
        services.AddSingleton<IRuleClaimsAccessor>(NullRuleClaimsAccessor.Instance);  // default null claims

        var options = new WincheDatabaseOptions(services);
        configure(options);

        var connectionString = !string.IsNullOrWhiteSpace(options.ConnectionString)
            ? options.ConnectionString
            : throw new InvalidOperationException(
                $"{nameof(WincheDatabaseOptions)}.{nameof(WincheDatabaseOptions.ConnectionString)} is required.");

        services.AddSingleton(Options.Create(options));

        services.AddNpgsqlDataSource(connectionString, serviceKey: WincheDatabaseKeys.DataSource);

        services.AddSingleton<CollectionIndexResolver>();
        services.AddSingleton(sp => new QueryListenerRegistry(
            sp.GetRequiredKeyedService<NpgsqlDataSource>(WincheDatabaseKeys.DataSource),
            sp.GetRequiredService<CollectionIndexResolver>(),
            sp.GetRequiredService<IOptions<WincheDatabaseOptions>>().Value.QueryConfig.StatementTimeout,
            sp.GetRequiredService<IOptions<WincheDatabaseOptions>>().Value.QueryLimits)
        );
        services.AddSingleton(sp => new DocumentListenerRegistry(
            sp.GetRequiredKeyedService<NpgsqlDataSource>(WincheDatabaseKeys.DataSource)));
        // Plain (unguarded) core — for privileged reads (e.g. ListCollectionIdsAsync). Its TransactionLedger
        // is NOT shared with the guarded core registered below; do not cross transaction handles between them.
        // Registered under a KEYED service (WincheDatabaseKeys.PrivilegedCore) rather than a plain singleton
        // so it is un-gettable BY ACCIDENT: an unkeyed provider.GetService<DocumentDatabase>() returns null,
        // and only a consumer that deliberately asks for the key (or the interface IDocumentDatabase, which
        // resolves to the RULE-GUARDED core below) gets a working database.
        services.AddKeyedSingleton(WincheDatabaseKeys.PrivilegedCore, (sp, _) => new DocumentDatabase(
            sp.GetRequiredKeyedService<NpgsqlDataSource>(WincheDatabaseKeys.DataSource),
            sp.GetRequiredService<IOptions<WincheDatabaseOptions>>(),
            sp.GetRequiredService<QueryListenerRegistry>(),
            sp.GetRequiredService<CollectionIndexResolver>(),
            docListeners: sp.GetRequiredService<DocumentListenerRegistry>()
        ));

        services.AddSingleton<ISchemaManager, SchemaManager>();

        // Change feed consumers
        services.AddSingleton<IChangeFeedConsumer>(sp => sp.GetRequiredService<QueryListenerRegistry>());
        services.AddSingleton<IChangeFeedConsumer>(sp => sp.GetRequiredService<DocumentListenerRegistry>());
        services.AddSingleton<IChangeFeedConsumer>(sp => new HookFeedConsumer(
            sp.GetRequiredService<IEnumerable<HookRegistration>>()));

        services.AddHostedService<ChangeFeedHostedService>();
        services.AddHostedService<RetentionPruner>();
        services.AddHostedService<TransactionSweeper>();
        services.AddHostedService<TtlSweeper>();

        // ── Rules guard ─────────────────────────────────────────────────────────
        // This package owns an isolated rules engine, registered under a package-specific key so the
        // Database engine and the Storage engine never merge. Built from this package's own UseRules
        // rulesets (empty => deny-all), using the engine-faithful comparer. The engine is hot-swappable
        // via a mutable rule-set repository, still isolated under the package-specific key.
        services.AddWincheRules(WincheDatabaseKeys.RuleEngine, o => o
            .WithRuleValueComparer(WincheRuleValueComparer.Instance)
            .WithMutableRuleSetRepository(RuleSet.Merge(options.Rulesets)));

        services.AddSingleton<IWriteAuthorizer>(sp => new RulesWriteAuthorizer(
            sp.GetRequiredKeyedService<RuleEngine>(WincheDatabaseKeys.RuleEngine),
            sp.GetRequiredService<IRuleClaimsAccessor>())
        );

        // Plain DocumentDatabase (above) stays the unguarded bypass core. The read-guard wraps a
        // separate authorizing core (writes go through IWriteAuthorizer inside the transaction).
        services.AddSingleton(sp =>
            new RuleGuardedDocumentDatabase(
                new DocumentDatabase(
                    sp.GetRequiredKeyedService<NpgsqlDataSource>(WincheDatabaseKeys.DataSource),
                    sp.GetRequiredService<IOptions<WincheDatabaseOptions>>(),
                    sp.GetRequiredService<QueryListenerRegistry>(),
                    sp.GetRequiredService<CollectionIndexResolver>(),
                    sp.GetRequiredService<IWriteAuthorizer>(),
                    docListeners: sp.GetRequiredService<DocumentListenerRegistry>()
                ),
                sp.GetRequiredKeyedService<RuleEngine>(WincheDatabaseKeys.RuleEngine),
                sp.GetRequiredService<IRuleClaimsAccessor>(),
                sp.GetRequiredService<IOptions<WincheDatabaseOptions>>())
            );

        services.AddSingleton<IDocumentDatabase>(sp =>
            sp.GetRequiredService<RuleGuardedDocumentDatabase>()
        );

        return services;
    }
}
