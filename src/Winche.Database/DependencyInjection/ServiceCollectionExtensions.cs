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
        services.AddSingleton<IRuleClaimsAccessor>(WincheCallerClaimsAccessor.Instance);  // the request's caller (WincheCaller)
        DatabaseChangesExtensions.AddDatabaseActions(services);   // database.deleteDocuments, for hooks

        var options = new WincheDatabaseOptions(services);
        configure(options);

        var connectionString = !string.IsNullOrWhiteSpace(options.ConnectionString)
            ? options.ConnectionString
            : throw new InvalidOperationException(
                $"{nameof(WincheDatabaseOptions)}.{nameof(WincheDatabaseOptions.ConnectionString)} is required.");

        services.AddSingleton(Options.Create(options));

        services.AddNpgsqlDataSource(connectionString, serviceKey: WincheDatabaseKeys.DataSource);

        // The indexes and expiry policies in force: the code's, or the console's with UseSettings().
        Winche.Core.SettingsArea<Settings.DatabaseSettings>? area = null;
        if (options.SettingsEnabled)
        {
            var codeIndexes = services.Where(d => d.ServiceType == typeof(Documents.IndexDefinition) && d.ImplementationInstance is not null)
                .Select(d => (Documents.IndexDefinition)d.ImplementationInstance!).ToList();
            var codeTtl = services.Where(d => d.ServiceType == typeof(Runtime.Ttl.TtlPolicy) && d.ImplementationInstance is not null)
                .Select(d => (Runtime.Ttl.TtlPolicy)d.ImplementationInstance!).ToList();
            area = Winche.Core.ServiceCollectionExtensions.AddWincheSettingsArea(services, Settings.DatabaseSettings.Area, "Database",
                Settings.DatabaseSettings.From(options, codeIndexes, codeTtl), s => s.Problems());
            area.Changed += s => s.ApplyTo(options);
            services.AddHostedService<Settings.IndexSyncService>();
        }
        services.AddSingleton(sp =>
        {
            var catalog = new IndexCatalog(sp.GetServices<Documents.IndexDefinition>(), sp.GetServices<Runtime.Ttl.TtlPolicy>());
            if (area is not null)
            {
                void Follow(Settings.DatabaseSettings s) => catalog.Replace(s.IndexDefinitions(), s.TtlPolicies());
                Follow(area.Current);
                area.Changed += Follow;
            }
            return catalog;
        });
        services.AddSingleton(sp => new CollectionIndexResolver(sp.GetRequiredService<IndexCatalog>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<CollectionIndexResolver>>(), sp.GetRequiredService<IOptions<WincheDatabaseOptions>>()));
        services.AddSingleton(sp => new QueryListenerRegistry(
            sp.GetRequiredKeyedService<NpgsqlDataSource>(WincheDatabaseKeys.DataSource),
            sp.GetRequiredService<CollectionIndexResolver>(),
            options.Listeners)
        );
        services.AddSingleton(sp => new DocumentListenerRegistry(
            sp.GetRequiredKeyedService<NpgsqlDataSource>(WincheDatabaseKeys.DataSource),
            options.Listeners));
        // Plain (unguarded) core — for privileged reads (e.g. ListCollectionIdsAsync). With the default
        // TransactionStore.Database both cores (and every node) share transactions; with Memory they don't.
        services.AddSingleton(sp => new DocumentDatabase(
            sp.GetRequiredKeyedService<NpgsqlDataSource>(WincheDatabaseKeys.DataSource),
            sp.GetRequiredService<IOptions<WincheDatabaseOptions>>(),
            sp.GetRequiredService<QueryListenerRegistry>(),
            sp.GetRequiredService<CollectionIndexResolver>(),
            writeAuthorizer: null,
            docListeners: sp.GetRequiredService<DocumentListenerRegistry>(),
            claims: sp.GetRequiredService<IRuleClaimsAccessor>()
        ) { BeforeHooks = sp.GetRequiredService<Abstraction.BeforeWriteHooks>() });
        services.AddSingleton(sp => new Abstraction.BeforeWriteHooks(sp.GetServices<Abstraction.BeforeHookRegistration>(),
            () => sp.GetRequiredService<IRuleClaimsAccessor>().GetClaims()));
        services.AddSingleton<Runtime.History.DocumentHistory>();

        services.AddSingleton<ISchemaManager, SchemaManager>();

        // Change feed consumers
        services.AddSingleton<IChangeFeedConsumer>(sp => sp.GetRequiredService<QueryListenerRegistry>());
        services.AddSingleton<IChangeFeedConsumer>(sp => sp.GetRequiredService<DocumentListenerRegistry>());
        // Hooks run as one durable consumer per hook type (own cursor each), built by the set.
        services.AddSingleton(sp => new ChangeFeedConsumerSet(
            sp.GetServices<IChangeFeedConsumer>(), sp.GetServices<HookRegistration>()));
        services.AddSingleton<ChangeFeedConsumers>();
        services.AddSingleton<Winche.Core.Feeds.IWincheFeedConsumers>(sp => sp.GetRequiredService<ChangeFeedConsumers>());   // for the console

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
            .WithMutableRuleSetRepository(RuleSet.Merge(options.Rulesets)));

        // expand's file:/user:/… resolvers (Winche.Storage, Winche Auth, the app's own register them).
        services.AddSingleton(sp => new Runtime.Expansion.ReferenceResolvers(sp.GetServices<IWincheReferenceResolver>(),
            sp.GetRequiredService<IRuleClaimsAccessor>(), sp.GetRequiredKeyedService<NpgsqlDataSource>(WincheDatabaseKeys.DataSource),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<Runtime.Expansion.ReferenceResolvers>>()));

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
                    docListeners: sp.GetRequiredService<DocumentListenerRegistry>(),
                    claims: sp.GetRequiredService<IRuleClaimsAccessor>()
                ) { BeforeHooks = sp.GetRequiredService<Abstraction.BeforeWriteHooks>() },
                sp.GetRequiredKeyedService<RuleEngine>(WincheDatabaseKeys.RuleEngine),
                sp.GetRequiredService<IRuleClaimsAccessor>(),
                options.RequestLog is not null || options.SettingsEnabled
                    ? new Runtime.Logging.RequestLog(
                        (sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>() ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance)
                            .CreateLogger(Runtime.Logging.RequestLog.Category),
                        () => options.RequestLog, sp.GetRequiredService<IRuleClaimsAccessor>())
                    : null)
            );

        services.AddSingleton<IDocumentDatabase>(sp =>
            sp.GetRequiredService<RuleGuardedDocumentDatabase>()
        );

        return services;
    }
}
