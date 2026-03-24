using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using WincheDb.DocumentStore.Abstraction;
using WincheDb.Realtime.Handlers;
using WincheDb.Realtime.Messages;
using WincheDb.Realtime.Operands;
using WincheDb.Realtime.Stores;

namespace WincheDb.Realtime.Services;

public sealed class MessageRouter(
    ConnectionClaimsStore connectionClaimsStore,
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
    ILogger<MessageRouter> logger
)
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    public async Task HandleMessageAsync(string connectionId, WebSocketConnection connection, JsonDocument document, CancellationToken ct)
    {
        CallerContext.Claims = connectionClaimsStore.GetClaims(connectionId);

        ClientMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<ClientMessage>(document.RootElement, _jsonOptions);
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
                Message = "Invalid message format"
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
                Message = "Access denied"
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
