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
using Winche.Database.Schema;
using Winche.Rules;

namespace Winche.Database.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWincheDatabase(
        this IServiceCollection services, Action<WincheDatabaseOptions> configure)
    {
        // Register defaults BEFORE calling configure, so that any UseRules() / MapClaims()
        // call inside configure adds registrations AFTER these. .NET DI's GetRequiredService<T>()
        // returns the last-registered singleton, so the user's choices win.
        services.AddSingleton(RulesetBuilder.Build(_ => { }));          // default deny-all ruleset
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
        // RuleGuardedDocumentDatabase is both the default IDocumentDatabase and resolvable
        // by concrete type. It always wraps the CORE unguarded DocumentDatabase — no recursion.
        // All registered Ruleset singletons are merged so that multiple UseRules() calls accumulate
        // rather than overwrite: OR semantics across blocks means concatenation is semantically correct.
        services.AddSingleton<RuleGuardedDocumentDatabase>(sp =>
            new RuleGuardedDocumentDatabase(
                sp.GetRequiredService<DocumentDatabase>(),       // CORE unguarded db — no recursion
                Ruleset.Merge(sp.GetServices<Ruleset>()),
                () => sp.GetRequiredService<IRuleClaimsAccessor>().GetClaims()));

        // Default IDocumentDatabase resolves to the Rules guard.
        services.AddSingleton<IDocumentDatabase>(sp =>
            sp.GetRequiredService<RuleGuardedDocumentDatabase>());

        return services;
    }
}
