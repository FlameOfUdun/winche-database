using Microsoft.Extensions.DependencyInjection;
using WincheDatabase.Store.Abstraction;
using WincheDatabase.Ws.DependencyInjection;
using WincheDatabase.WS.Abstraction;
using WincheDatabase.WS.Handlers;
using WincheDatabase.WS.Messages;
using WincheDatabase.WS.Services;

namespace WincheDatabase.WS.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWincheDatabaseWsApi(this IServiceCollection services, Action<DependencyConfigurator>? configure = null)
    {
        var configurator = new DependencyConfigurator(services);
        configure?.Invoke(configurator);

        services.AddSingleton<IConnectionRegistry, ConnectionRegistry>();
        services.AddSingleton<IConnectionClaimsStore, ConnectionClaimsStore>();
        services.AddSingleton<IConnectionManager, ConnectionManager>();
        services.AddSingleton<IMessageRouter, MessageRouter>();
        services.AddSingleton<ISubscriptionEventHandler, EventDispatcher>();
        services.AddSingleton<ISubscriptionConnectionMap, SubscriptionConnectionMap>();
        services.AddSingleton<ITransactionConnectionMap, TransactionConnectionMap>();

        services.AddSingleton<IMessageHandler<SystemPingRequest>, SystemPingHandler>();
        services.AddSingleton<IMessageHandler<DocumentGetRequest>, DocumentGetHandler>();
        services.AddSingleton<IMessageHandler<DocumentSetRequest>, DocumentSetHandler>();
        services.AddSingleton<IMessageHandler<DocumentUpdateRequest>, DocumentUpdateHandler>();
        services.AddSingleton<IMessageHandler<DocumentDeleteRequest>, DocumentDeleteHandler>();
        services.AddSingleton<IMessageHandler<QueryExecuteRequest>, QueryExecuteHandler>();
        services.AddSingleton<IMessageHandler<QuerySubscribeRequest>, QuerySubscribeHandler>();
        services.AddSingleton<IMessageHandler<QueryUnsubscribeRequest>, QueryUnsubscribeHandler>();
        services.AddSingleton<IMessageHandler<TransactionBeginRequest>, TransactionBeginHandler>();
        services.AddSingleton<IMessageHandler<TransactionGetRequest>, TransactionGetHandler>();
        services.AddSingleton<IMessageHandler<TransactionSetRequest>, TransactionSetHandler>();
        services.AddSingleton<IMessageHandler<TransactionUpdateRequest>, TransactionUpdateHandler>();
        services.AddSingleton<IMessageHandler<TransactionDeleteRequest>, TransactionDeleteHandler>();
        services.AddSingleton<IMessageHandler<TransactionQueryRequest>, TransactionQueryHandler>();
        services.AddSingleton<IMessageHandler<TransactionCommitRequest>, TransactionCommitHandler>();
        services.AddSingleton<IMessageHandler<TransactionRollbackRequest>, TransactionRollbackHandler>();
        services.AddSingleton<IMessageHandler<BatchCommitRequest>, BatchCommitHandler>();
        services.AddSingleton<IMessageHandler<SyncPushRequest>, SyncPushHandler>();
        services.AddSingleton<IMessageHandler<AggregateExecuteRequest>, AggregateExecuteHandler>();

        return services;
    }
}