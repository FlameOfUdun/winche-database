using Microsoft.Extensions.DependencyInjection;
using Winche.Database.AspNetCore.WebSockets.Interfaces;
using Winche.Database.AspNetCore.WebSockets.Services;
using Winche.Database.AspNetCore.WebSockets.Handlers;
using Winche.Database.AspNetCore.WebSockets.Messages;
using Winche.Database.Interfaces;

namespace Winche.Database.AspNetCore.WebSockets.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWincheDatabaseWsApi(this IServiceCollection services)
    {
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