using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.WebSockets;
using WincheDatabase.Store.Services;
using WincheDatabase.Store.Stores;
using WincheDatabase.WS.Operands;
using WincheDatabase.WS.Stores;

namespace WincheDatabase.WS.Services;

public sealed class ConnectionManager(
    MessageRouter messageRouter,
    ConnectionRegistry connectionRegistry,
    ConnectionClaimsStore connectionClaimsStore,
    SubscriptionConnectionMap subscriptionConnectionMap,
    TransactionConnectionMap transactionConnectionMap,
    SubscriptionManager subscriptionManager,
    TransactionRegistry transactionRegistry,
    IOptions<JsonOptions> jsonOptions,
    ILogger<ConnectionManager> logger
)
{
    public async Task AcceptAsync(WebSocket socket, IReadOnlyDictionary<string, object?> claims)
    {
        var connectionId = Guid.NewGuid().ToString();
        var connection = new WebSocketConnection(socket, jsonOptions.Value.SerializerOptions);

        connectionClaimsStore.SetClaims(connectionId, claims);

        connectionRegistry.Add(connectionId, connection);

        try
        {
            while (connection.IsOpen)
            {
                try
                {
                    var message = await connection.ReceiveAsync(connection.CancellationToken);
                    if (message == null)
                    {
                        break;
                    }

                    await messageRouter.HandleMessageAsync(connectionId, connection, message, connection.CancellationToken);
                    message.Dispose();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "WebSocket error for connection {ConnectionId}", connectionId);
        }
        finally
        {
            connectionRegistry.Remove(connectionId);
            connectionClaimsStore.Remove(connectionId);

            foreach (var subId in subscriptionConnectionMap.UntrackAll(connectionId))
                subscriptionManager.Unsubscribe(subId);

            foreach (var txId in transactionConnectionMap.UntrackAll(connectionId))
            {
                if (transactionRegistry.TryRemove(txId, out var tx) && tx is not null)
                    await tx.DisposeAsync();
            }
        }
    }
}
