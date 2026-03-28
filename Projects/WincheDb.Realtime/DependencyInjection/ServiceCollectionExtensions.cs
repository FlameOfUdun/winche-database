using Microsoft.Extensions.DependencyInjection;
using WincheDb.DocumentStore.Abstraction;
using WincheDb.Realtime.Handlers;
using WincheDb.Realtime.Messages;
using WincheDb.Realtime.Services;
using WincheDb.Realtime.Stores;

namespace WincheDb.Realtime.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWincheDbRealtime(this IServiceCollection services)
    {
        services.AddSingleton<ConnectionRegistry>();
        services.AddSingleton<ConnectionClaimsStore>();
        services.AddSingleton<ConnectionManager>();
        services.AddSingleton<MessageRouter>();
        services.AddSingleton<ISubscriptionEventHandler, EventDispatcher>();

        services.AddSingleton<SubscriptionConnectionMap>();
        services.AddSingleton<TransactionConnectionMap>();

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

        return services;
    }
}