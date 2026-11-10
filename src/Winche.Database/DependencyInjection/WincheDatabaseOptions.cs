using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Winche.Database.Abstraction;
using Winche.Database.Documents;
using Winche.Database.Runtime.Ttl;
using Winche.Database.Runtime.Writes;
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

    /// <summary>
    /// Rulesets registered via <see cref="UseRules(RuleSet)"/>. Collected here (not in the DI
    /// container) so <c>AddWincheDatabase</c> can build an engine from this package's rules only.
    /// </summary>
    internal List<RuleSet> Rulesets { get; } = [];

    public TransactionConfig TransactionConfig { get; set; } = new();
    public ChangeFeedConfig ChangeFeed { get; set; } = new();
    public ListenerConfig Listeners { get; set; } = new();
    public WriteLimits WriteLimits { get; set; } = new();
    public TtlConfig Ttl { get; set; } = new();

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

    /// <summary>
    /// Registers one or more <see cref="TtlPolicy"/> instances via a fluent builder (mirrors
    /// <see cref="UseIndexes(Action{IndexBuilder})"/>). Multiple calls accumulate — each policy is a
    /// singleton so <c>GetServices&lt;TtlPolicy&gt;()</c> returns them all for the TTL sweeper at startup.
    /// </summary>
    public WincheDatabaseOptions UseTtl(Action<TtlBuilder> configure)
    {
        var builder = new TtlBuilder(Services);
        configure(builder);
        return this;
    }

    /// <summary>Convenience overload — registers the supplied policies directly.</summary>
    public WincheDatabaseOptions UseTtl(params TtlPolicy[] policies)
    {
        foreach (var policy in policies)
            Services.AddSingleton(policy);
        return this;
    }

    /// <summary>
    /// Registers document lifecycle hooks via a fluent builder, binding each hook to a
    /// path pattern at registration time (mirroring <see cref="UseIndexes(Action{IndexBuilder})"/>).
    /// Multiple calls accumulate — each binding is registered as a singleton so that
    /// <c>GetServices&lt;HookRegistration&gt;()</c> returns them all at startup.
    /// </summary>
    public WincheDatabaseOptions UseHooks(Action<HookBuilder> configure)
    {
        var builder = new HookBuilder(Services);
        configure(builder);
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
        Rulesets.Add(ruleset);
        return this;
    }

    /// <summary>
    /// Builds a <see cref="RuleSet"/> from a <see cref="RuleSetBuilder"/> delegate and adds it
    /// to the merged set of active rulesets. Shorthand for <c>UseRules(RuleSetBuilder.Build(configure))</c>.
    /// Multiple <c>UseRules</c> calls accumulate — each registered ruleset's blocks are
    /// OR-combined with all others. With no <c>UseRules</c> call, access is default-deny.
    /// </summary>
    public WincheDatabaseOptions UseRules(Action<RuleSetBuilder> configure)
    {
        Rulesets.Add(RuleSetBuilder.Build(configure));
        return this;
    }
}

public sealed record ChangeFeedConfig
{
    /// <summary>
    /// Feed rows older than this are pruned (default 7 days) — but only once every durable consumer
    /// (hook) has delivered them, so a consumer that is behind never loses changes to pruning, up to
    /// <see cref="MaxRetention"/>.
    /// </summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Hard limit (default 30 days): rows older than this are pruned even if a durable consumer hasn't
    /// delivered them yet (logged as an error), so a consumer that is stuck or gone can't grow the feed
    /// without bound.
    /// </summary>
    public TimeSpan MaxRetention { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// How long a durable consumer keeps retrying one change that keeps failing before it moves the change
    /// to the dead-letter table and carries on (default 1 hour; null retries for ever). The clock runs
    /// from the first failure and survives restarts. Dead letters can be listed and replayed through
    /// <see cref="Runtime.ChangeFeed.ChangeFeedConsumers"/>.
    /// </summary>
    public TimeSpan? DeadLetterAfter { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan PruneInterval { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>Poll fallback for missed notifies.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
    public int BatchSize { get; init; } = 500;

    /// <summary>
    /// Durable consumers (hooks) run on one node at a time, elected with a Postgres advisory lock.
    /// Nodes that don't hold it retry this often, so a new node takes over within about this long
    /// after the holder stops.
    /// </summary>
    public TimeSpan LeaderRetryInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the elected node checks that its lock connection is still alive.</summary>
    public TimeSpan LeaseHeartbeat { get; init; } = TimeSpan.FromSeconds(10);
}

public sealed record ListenerConfig
{
    /// <summary>
    /// Apply a change to a live query's result in memory when that gives the exact result, instead of
    /// re-running the query (default true). Queries with select, offset, limitToLast or cursors are always
    /// re-run, as are limited queries whose full window loses or changes a member.
    /// </summary>
    public bool ApplyChangesInMemory { get; init; } = true;

    /// <summary>
    /// A live query updated in memory re-runs its real query on the first change after this long
    /// (default 5 minutes), so any drift from the database is corrected.
    /// </summary>
    public TimeSpan ResyncInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How many live queries (and document listeners) a node updates at once per change batch (default 8).</summary>
    public int MaxParallelUpdates { get; init; } = 8;
}

public sealed record TtlConfig
{
    /// <summary>
    /// How often the TTL sweeper runs (default 5 min; managed TTL is typically best-effort (up to ~72h)).
    /// Must be &gt; <see cref="TimeSpan.Zero"/> (a zero/negative interval would busy-loop the sweeper).
    /// </summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Max documents deleted per collection per batch; the sweeper loops until a batch is short.
    /// The sweeper clamps this to <c>[1, WriteValidator.MaxBatchSize]</c> (500) — a delete batch above
    /// that cap is rejected by the write path, so larger values are capped rather than honored.
    /// </summary>
    public int BatchSize { get; init; } = 500;

    /// <summary>
    /// Whether a TTL delete also deletes the document's subcollections (cascade). Defaults to <c>true</c>.
    /// Set to <c>false</c> to delete only the matched document and leave its subcollection documents in place.
    /// </summary>
    public bool CascadeDelete { get; init; } = true;
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
    /// <paramref name="collectionId"/> and one or more <paramref name="fields"/>.
    /// </summary>
    public IndexBuilder Add(string collectionId, params IndexField[] fields)
    {
        services.AddSingleton(new IndexDefinition(collectionId, fields));
        return this;
    }
}

/// <summary>
/// Fluent builder used inside <see cref="WincheDatabaseOptions.UseTtl(Action{TtlBuilder})"/>. Each
/// <see cref="Add(TtlPolicy)"/> registers one <see cref="TtlPolicy"/> as a singleton so that
/// <c>GetServices&lt;TtlPolicy&gt;()</c> returns them all at startup.
/// </summary>
public sealed class TtlBuilder(IServiceCollection services)
{
    /// <summary>Adds a fully-constructed <see cref="TtlPolicy"/>.</summary>
    public TtlBuilder Add(TtlPolicy policy)
    {
        services.AddSingleton(policy);
        return this;
    }

    /// <summary>Convenience overload: creates and registers a policy from a collection id and field path.</summary>
    public TtlBuilder Add(string collectionId, string field)
    {
        services.AddSingleton(TtlPolicy.For(collectionId, field));
        return this;
    }
}

/// <summary>
/// Fluent builder used inside <see cref="WincheDatabaseOptions.UseHooks(Action{HookBuilder})"/>.
/// Each <see cref="Add{THook}(string)"/> call registers one <see cref="HookRegistration"/> as a
/// singleton so that <c>GetServices&lt;HookRegistration&gt;()</c> returns them all at startup.
/// </summary>
public sealed class HookBuilder(IServiceCollection services)
{
    /// <summary>
    /// Registers a hook <typeparamref name="THook"/> (constructed via DI, so constructor injection
    /// works) bound to <paramref name="path"/>. The same hook type may be bound to multiple paths.
    /// </summary>
    public HookBuilder Add<THook>(string path) where THook : DocumentStoreHook
    {
        services.TryAddSingleton<THook>();
        services.AddSingleton(sp => new HookRegistration(path, sp.GetRequiredService<THook>()));
        return this;
    }

    /// <summary>Registers a pre-constructed <paramref name="hook"/> instance bound to <paramref name="path"/>.</summary>
    public HookBuilder Add(string path, DocumentStoreHook hook)
    {
        services.AddSingleton(new HookRegistration(path, hook));
        return this;
    }
}
