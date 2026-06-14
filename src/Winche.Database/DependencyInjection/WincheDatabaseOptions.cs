using Microsoft.Extensions.DependencyInjection;
using Winche.Database.Abstraction;
using Winche.Database.Documents;
using Winche.Rules;

namespace Winche.Database.DependencyInjection;

/// <summary>
/// The single options surface for Winche.Database: connection, store behavior, and component
/// registrations (access rules, hooks, index definitions). Configured through the
/// <c>AddWincheDatabase</c> lambda and consumed at runtime via <c>IOptions&lt;WincheDatabaseOptions&gt;</c>.
/// Transport packages extend this type (e.g. <c>MapClaims</c>) through <see cref="Services"/>.
/// </summary>
public sealed class WincheDatabaseOptions
{
    private readonly IServiceCollection? _services;

    public WincheDatabaseOptions() { }

    internal WincheDatabaseOptions(IServiceCollection services) => _services = services;

    /// <summary>Registration handle for extension packages; available only inside the AddWincheDatabase lambda.</summary>
    public IServiceCollection Services => _services
        ?? throw new InvalidOperationException("Services is available only within the AddWincheDatabase configuration lambda.");

    /// <summary>
    /// Required. Postgres connection string. All objects live in the connection's search_path
    /// schema — use <c>Search Path=myschema</c> here for non-<c>public</c> deployments.
    /// </summary>
    public string? ConnectionString { get; set; }

    public TransactionConfig TransactionConfig { get; set; } = new();
    public ChangeFeedConfig ChangeFeed { get; set; } = new();

    /// <summary>
    /// Registers one or more <see cref="IndexDefinition"/> instances. Multiple calls accumulate —
    /// each definition is registered as a singleton so that <c>GetServices&lt;IndexDefinition&gt;()</c>
    /// returns them all at startup.
    /// </summary>
    public WincheDatabaseOptions UseIndexes(Action<IndexBuilder> configure)
    {
        var builder = new IndexBuilder(Services);
        configure(builder);
        return this;
    }

    /// <summary>Convenience overload — registers the supplied definitions directly.</summary>
    public WincheDatabaseOptions UseIndexes(params IndexDefinition[] definitions)
    {
        foreach (var def in definitions)
            Services.AddSingleton(def);
        return this;
    }

    public WincheDatabaseOptions AddHook<THook>() where THook : DocumentStoreHook
    {
        Services.AddSingleton<DocumentStoreHook, THook>();
        return this;
    }

    /// <summary>
    /// Adds a <see cref="RuleSet"/> to the Winche.Rules guard
    /// (<see cref="Authorization.RuleGuardedDocumentDatabase"/>), which is the default
    /// <see cref="Runtime.IDocumentDatabase"/> since Phase 4b.
    /// The ruleset is registered as a singleton in the DI container and <strong>merged</strong>
    /// with any other rulesets registered via previous or subsequent <c>UseRules</c> calls
    /// (including the default empty deny-all ruleset that <c>AddWincheDatabase</c> installs
    /// automatically). Multiple <c>UseRules</c> calls accumulate: each call's blocks are
    /// OR-combined with all others. With no <c>UseRules</c> call, access is default-deny.
    /// </summary>
    public WincheDatabaseOptions UseRules(RuleSet ruleset)
    {
        Services.AddSingleton(ruleset);
        return this;
    }

    /// <summary>
    /// Builds a <see cref="RuleSet"/> from a <see cref="RulesetBuilder"/> delegate and adds it
    /// to the merged set of active rulesets. Shorthand for <c>UseRules(RulesetBuilder.Build(configure))</c>.
    /// Multiple <c>UseRules</c> calls accumulate — each registered ruleset's blocks are
    /// OR-combined with all others. With no <c>UseRules</c> call, access is default-deny.
    /// </summary>
    public WincheDatabaseOptions UseRules(Action<RulesetBuilder> configure)
    {
        var ruleset = RulesetBuilder.Build(configure);
        Services.AddSingleton(ruleset);
        return this;
    }
}

public sealed record ChangeFeedConfig
{
    /// <summary>Feed rows older than this are pruned (spec §4; default 7 days).</summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan PruneInterval { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>Poll fallback for missed notifies.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
    public int BatchSize { get; init; } = 500;
}

public sealed record TransactionConfig
{
    public TimeSpan TotalTimeoutSpan { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Spec (runtime §3): optimistic transactions idle out after 60 seconds by default.</summary>
    public TimeSpan IdleTimeoutSpan { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Fluent builder used inside <see cref="WincheDatabaseOptions.UseIndexes(Action{IndexBuilder})"/>.
/// Each <see cref="Add"/> call registers one <see cref="IndexDefinition"/> as a singleton so
/// that <c>GetServices&lt;IndexDefinition&gt;()</c> returns them all at startup.
/// </summary>
public sealed class IndexBuilder(IServiceCollection services)
{
    /// <summary>Adds a fully-constructed <see cref="IndexDefinition"/>.</summary>
    public IndexBuilder Add(IndexDefinition definition)
    {
        services.AddSingleton(definition);
        return this;
    }

    /// <summary>
    /// Convenience overload: creates and registers an <see cref="IndexDefinition"/> from
    /// <paramref name="path"/> and one or more <paramref name="fields"/>.
    /// </summary>
    public IndexBuilder Add(string path, params IndexField[] fields)
    {
        services.AddSingleton(new IndexDefinition(path, fields));
        return this;
    }
}
