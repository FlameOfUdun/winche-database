using Microsoft.Extensions.DependencyInjection;
using Winche.Database.Abstraction;
using Winche.Database.Documents;
using Winche.Sentinel.DependencyInjection;

namespace Winche.Database.DependencyInjection;

public sealed class DependencyConfigurator(IServiceCollection services)
{
    public IServiceCollection Services => services;

    public DependencyConfigurator AddDocumentAccessRule<TRule>() where TRule : DocumentAccessRule
    {
        services.ConfigureWincheSentinel<Document>(configurator =>
        {
            configurator.AddResourceAccessRule<TRule>();
        });
        return this;
    }

    public DependencyConfigurator AddIndexDefinition<TIndex>() where TIndex : IndexDefinition
    {
        services.AddSingleton<IndexDefinition, TIndex>();
        return this;
    }

    public DependencyConfigurator AddDocumentStoreHook<THook>() where THook : DocumentStoreHook
    {
        services.AddSingleton<DocumentStoreHook, THook>();
        return this;
    }
}
