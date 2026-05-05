using Microsoft.Extensions.DependencyInjection;
using WincheDatabase.AST.Models;
using WincheDatabase.Core.Models;
using WincheDatabase.Store.Abstraction;
using WincheDatabase.Store.Models;
using WincheSentinel.Core.DependencyInjection;

namespace WincheDatabase.Store.DependencyInjection;

public sealed class DependencyConfigurator(IServiceCollection services)
{
    public DependencyConfigurator AddDocumentAccessRule<TRule>() where TRule : DocumentAccessRule
    {
        services.ConfigureWincheSentinel<Document>(configurator =>
        {
            configurator.AddAccessRule<TRule>();
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
