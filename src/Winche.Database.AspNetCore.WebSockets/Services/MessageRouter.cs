using Microsoft.Extensions.Logging;
using System.Text.Json;
using Winche.Sentinel.Models;
using Winche.Database.AspNetCore.WebSockets.Interfaces;
using Winche.Database.AspNetCore.WebSockets.Messages;
using Winche.Database.AspNetCore.WebSockets.Operands;
using Winche.Database.Services;
using Winche.Sentinel.Interfaces;
using Winche.Database.AspNetCore.Abstraction;

namespace Winche.Database.AspNetCore.WebSockets.Services;

public sealed class MessageRouter(
    IConnectionClaimsStore connectionClaimsStore,
    IMessageHandler<SystemPingRequest> systemPingHandler,
    IMessageHandler<DocumentGetRequest> documentGetHandler,
    IMessageHandler<DocumentSetRequest> documentSetHandler,
    IMessageHandler<DocumentUpdateRequest> documentUpdateHandler,
    IMessageHandler<DocumentDeleteRequest> documentDeleteHandler,
    IMessageHandler<QuerySubscribeRequest> querySubscribeHandler,
    IMessageHandler<QueryUnsubscribeRequest> queryUnsubscribeHandler,
    IMessageHandler<QueryExecuteRequest> queryExecuteHandler,
    IMessageHandler<TransactionBeginRequest> transactionBeginHandler,
    IMessageHandler<TransactionCommitRequest> transactionCommitHandler,
    IMessageHandler<TransactionRollbackRequest> transactionRollbackHandler,
    IMessageHandler<TransactionGetRequest> transactionGetHandler,
    IMessageHandler<TransactionSetRequest> transactionSetHandler,
    IMessageHandler<TransactionUpdateRequest> transactionUpdateHandler,
    IMessageHandler<TransactionDeleteRequest> transactionDeleteHandler,
    IMessageHandler<BatchCommitRequest> batchCommitHandler,
    IMessageHandler<SyncPushRequest> syncPushHandler,
    IMessageHandler<AggregateExecuteRequest> aggregateExecuteHandler,
    ILogger<MessageRouter> logger,
    DocumentClaimsAccessor callerClaimsAccessor
) : IMessageRouter
{
    public async Task HandleMessageAsync(string connectionId, WebSocketConnection connection, JsonDocument document, CancellationToken ct)
    {
        var claims = connectionClaimsStore.GetClaims(connectionId);
        callerClaimsAccessor.SetClaims(claims);

        ClientMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<ClientMessage>(document.RootElement);
            if (message == null)
            {
                logger.LogWarning("Received invalid request");
                await connection.SendAsync(new SystemErrorResponse
                {
                    RequestId = document.RootElement.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String ? idProp.GetString()! : "",
                    Message = "Invalid message format"
                }, ct);
                return;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deserializing request");
            await connection.SendAsync(new SystemErrorResponse
            {
                RequestId = document.RootElement.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String ? idProp.GetString()! : "",
                Message = ex.Message,
            }, ct);
            return;
        }

        try
        {
            var response = message switch
            {
                SystemPingRequest request => await systemPingHandler.HandleAsync(connectionId, request, ct),
                DocumentGetRequest request => await documentGetHandler.HandleAsync(connectionId, request, ct),
                DocumentSetRequest request => await documentSetHandler.HandleAsync(connectionId, request, ct),
                DocumentUpdateRequest request => await documentUpdateHandler.HandleAsync(connectionId, request, ct),
                DocumentDeleteRequest request => await documentDeleteHandler.HandleAsync(connectionId, request, ct),
                QuerySubscribeRequest request => await querySubscribeHandler.HandleAsync(connectionId, request, ct),
                QueryUnsubscribeRequest request => await queryUnsubscribeHandler.HandleAsync(connectionId, request, ct),
                QueryExecuteRequest request => await queryExecuteHandler.HandleAsync(connectionId, request, ct),
                TransactionBeginRequest request => await transactionBeginHandler.HandleAsync(connectionId, request, ct),
                TransactionCommitRequest request => await transactionCommitHandler.HandleAsync(connectionId, request, ct),
                TransactionRollbackRequest request => await transactionRollbackHandler.HandleAsync(connectionId, request, ct),
                TransactionGetRequest request => await transactionGetHandler.HandleAsync(connectionId, request, ct),
                TransactionSetRequest request => await transactionSetHandler.HandleAsync(connectionId, request, ct),
                TransactionUpdateRequest request => await transactionUpdateHandler.HandleAsync(connectionId, request, ct),
                TransactionDeleteRequest request => await transactionDeleteHandler.HandleAsync(connectionId, request, ct),
                BatchCommitRequest request => await batchCommitHandler.HandleAsync(connectionId, request, ct),
                SyncPushRequest request => await syncPushHandler.HandleAsync(connectionId, request, ct),
                AggregateExecuteRequest request => await aggregateExecuteHandler.HandleAsync(connectionId, request, ct),
                _ => new SystemErrorResponse
                {
                    RequestId = message.Id,
                    Message = $"Unknown request type: {message.GetType().Name}"
                },
            };

            await connection.SendAsync(response, ct);
        }
        catch (AccessDeniedException)
        {
            await connection.SendAsync(new SystemErrorResponse
            {
                RequestId = message.Id,
                Code = "permission_denied",
                Message = "Access denied to path"
            }, ct);
        }
        catch (NoRulesMatchedException)
        {
            await connection.SendAsync(new SystemErrorResponse
            {
                RequestId = message.Id,
                Code = "permission_denied",
                Message = "No rule matched the path"
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling message from connection {ConnectionId}", connectionId);

            await connection.SendAsync(new SystemErrorResponse
            {
                RequestId = message.Id,
                Message = ex.Message,
            }, ct);
        }
    }
}
