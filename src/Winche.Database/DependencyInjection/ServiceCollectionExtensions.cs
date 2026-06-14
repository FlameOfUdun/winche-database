using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Winche.Database.Abstraction;
using Winche.Database.Authorization;
using Winche.Database.Constants;
using Winche.Database.DependencyInjection;
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

        services.AddSingleton<IOptions<WincheDatabaseOptions>>(Options.Create(options));

        services.AddNpgsqlDataSource(connectionString, serviceKey: ServiceKeys.DATA_SOURCE_KEY);

        // Core runtime (spec architecture): rule-free core + guard as the public surface
        services.AddSingleton<Querying.IndexScopeResolver>();
        services.AddSingleton<ListenerRegistry>(sp => new ListenerRegistry(
            sp.GetRequiredKeyedService<NpgsqlDataSource>(ServiceKeys.DATA_SOURCE_KEY),
            sp.GetRequiredService<Querying.IndexScopeResolver>()));
        services.AddSingleton<DocumentDatabase>(sp => new DocumentDatabase(
            sp.GetRequiredKeyedService<NpgsqlDataSource>(ServiceKeys.DATA_SOURCE_KEY),
            sp.GetRequiredService<IOptions<WincheDatabaseOptions>>(),
            sp.GetRequiredService<ListenerRegistry>(),
            sp.GetRequiredService<Querying.IndexScopeResolver>()));

        services.AddSingleton<ISchemaManager, SchemaManager>();

        // Native path-pattern matcher (used by IndexScopeResolver and HookFeedConsumer)
        services.AddSingleton<IPathPatternMatcher>(PathPatternMatcher.Instance);

        // Change feed consumers
        services.AddSingleton<IChangeFeedConsumer>(sp => sp.GetRequiredService<ListenerRegistry>());
        services.AddSingleton<IChangeFeedConsumer>(sp => new HookFeedConsumer(
            sp.GetRequiredService<IEnumerable<DocumentStoreHook>>(),
            sp.GetRequiredService<IPathPatternMatcher>()));

        services.AddHostedService<ChangeFeedHostedService>();
        services.AddHostedService<RetentionPruner>();
        services.AddHostedService<TransactionSweeper>();

        // ── Winche.Rules guard ─────────────────────────────────────────────────
        // Register the engine via Winche.Rules DI. It merges every RuleSet registered by UseRules()
        // (plus the default deny-all seed below) and uses the database's engine-faithful comparer.
        services.AddWincheRules(o => o
            .WithComparer(WincheRuleValueComparer.Instance)
            .WithRuleset(_ => { }));                                     // default deny-all seed

        services.AddSingleton<IWriteAuthorizer>(sp => new RulesWriteAuthorizer(
            sp.GetRequiredService<RuleEngine>(),
            () => sp.GetRequiredService<IRuleClaimsAccessor>().GetClaims()));

        // Plain DocumentDatabase (above) stays the unguarded bypass core. The read-guard wraps a
        // separate authorizing core (writes go through IWriteAuthorizer inside the transaction).
        services.AddSingleton<RuleGuardedDocumentDatabase>(sp =>
            new RuleGuardedDocumentDatabase(
                new DocumentDatabase(
                    sp.GetRequiredKeyedService<NpgsqlDataSource>(ServiceKeys.DATA_SOURCE_KEY),
                    sp.GetRequiredService<IOptions<WincheDatabaseOptions>>(),
                    sp.GetRequiredService<ListenerRegistry>(),
                    sp.GetRequiredService<Querying.IndexScopeResolver>(),
                    sp.GetRequiredService<IWriteAuthorizer>()),
                sp.GetRequiredService<RuleEngine>(),
                () => sp.GetRequiredService<IRuleClaimsAccessor>().GetClaims()));

        services.AddSingleton<IDocumentDatabase>(sp =>
            sp.GetRequiredService<RuleGuardedDocumentDatabase>());

        return services;
    }
}
