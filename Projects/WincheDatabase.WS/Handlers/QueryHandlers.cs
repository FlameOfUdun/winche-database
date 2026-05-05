using WincheDatabase.Store.Interfaces;
using WincheDatabase.WS.Interfaces;
using WincheDatabase.WS.Messages;

namespace WincheDatabase.WS.Handlers;

internal sealed class QueryExecuteHandler(
    IDocumentManager documentManager
) : IMessageHandler<QueryExecuteRequest>
{
    public async Task<ServerMessage> HandleAsync(string connectionId, QueryExecuteRequest request, CancellationToken ct)
    {
        var result = await documentManager.QueryAsync(request.Query, ct);

        return new QueryExecuteResponse
        {
            RequestId = request.Id,
            Result = result,
        };
    }
}

internal sealed class QuerySubscribeHandler(
    ISubscriptionManager subscriptionManager,
    ISubscriptionConnectionMap subscriptionConnectionMap
) : IMessageHandler<QuerySubscribeRequest>
{
    public async Task<ServerMessage> HandleAsync(string connectionId, QuerySubscribeRequest request, CancellationToken ct)
    {
        var subscription = await subscriptionManager.SubscribeAsync(request.Query, ct);
        subscriptionConnectionMap.Track(connectionId, subscription.Id);

        return new QuerySubscribeResponse
        {
            RequestId = request.Id,
            SubscriptionId = subscription.Id,
            Result = subscription.Result
        };
    }
}

public sealed class QueryUnsubscribeHandler(
    ISubscriptionManager subscriptionManager,
    ISubscriptionConnectionMap subscriptionConnectionMap
) : IMessageHandler<QueryUnsubscribeRequest>
{
    public Task<ServerMessage> HandleAsync(string connectionId, QueryUnsubscribeRequest request, CancellationToken ct)
    {
        if (!subscriptionConnectionMap.TryGetOwner(request.SubscriptionId, out var owner) || owner != connectionId)
        {
            return Task.FromResult<ServerMessage>(new SystemErrorResponse
            {
                RequestId = request.Id,
                Code = "subscription_not_found",
                Message = $"Subscription with ID {request.SubscriptionId} not found"
            });
        }

        subscriptionConnectionMap.Untrack(connectionId, request.SubscriptionId);
        subscriptionManager.Unsubscribe(request.SubscriptionId);

        return Task.FromResult<ServerMessage>(new QueryUnsubscribeResponse
        {
            RequestId = request.Id,
            SubscriptionId = request.SubscriptionId
        });
    }
}
