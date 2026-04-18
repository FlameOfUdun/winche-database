using Microsoft.Extensions.Logging;
using System.Net.WebSockets;
using WincheDatabase.Store.Abstraction;
using WincheDatabase.WS.Abstraction;
using WincheDatabase.WS.Operands;

namespace WincheDatabase.WS.Services;

public sealed class ConnectionManager(
    IMessageRouter messageRouter,
    IConnectionRegistry connectionRegistry,
    IConnectionClaimsStore connectionClaimsStore,
    ISubscriptionConnectionMap subscriptionConnectionMap,
    ITransactionConnectionMap transactionConnectionMap,
    ISubscriptionManager subscriptionManager,
    ITransactionRegistry transactionRegistry,
    ILogger<ConnectionManager> logger
) : IConnectionManager
{
    public async Task AcceptAsync(WebSocket socket, IReadOnlyDictionary<string, object?> claims)
    {
        var connectionId = Guid.NewGuid().ToString();
        var connection = new WebSocketConnection(socket);

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
