using Microsoft.Extensions.DependencyInjection;
using Winche.Database.Abstraction;
using Winche.Database.Documents;
using Winche.Sentinel.DependencyInjection;

namespace Winche.Database.DependencyInjection;

/// <summary>
/// The single options surface for Winche.Database: connection, store behavior, and component
/// registrations (access rules, hooks, index definitions). Configured through the
/// <c>AddWincheDatabase</c> lambda and consumed at runtime via <c>IOptions&lt;WincheDatabaseOptions&gt;</c>.
/// Transport packages extend this type (e.g. <c>SetCallerClaimsAccessor</c>) through <see cref="Services"/>.
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

    public WincheDatabaseOptions AddDocumentAccessRule<TRule>() where TRule : DocumentAccessRule
    {
        Services.ConfigureWincheSentinel<Document>(configurator =>
        {
            configurator.AddResourceAccessRule<TRule>();
        });
        return this;
    }

    public WincheDatabaseOptions AddIndexDefinition<TIndex>() where TIndex : IndexDefinition
    {
        Services.AddSingleton<IndexDefinition, TIndex>();
        return this;
    }

    public WincheDatabaseOptions AddDocumentStoreHook<THook>() where THook : DocumentStoreHook
    {
        Services.AddSingleton<DocumentStoreHook, THook>();
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
