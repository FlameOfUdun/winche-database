using Winche.Database.AspNetCore.WebSockets.Messages;
using Winche.Database.AspNetCore.WebSockets.Operands;

namespace Winche.Database.AspNetCore.WebSockets.Interfaces;

public interface IConnectionRegistry
{
    void Add(string connectionId, WebSocketConnection connection);
    void Remove(string connectionId);
    bool TryGet(string connectionId, out WebSocketConnection? connection);
    Task SendAsync<T>(string connectionId, T message, CancellationToken ct = default) where T : ServerMessage;
    Task SendBatchAsync<T>(string connectionId, IReadOnlyList<T> messages, CancellationToken ct = default) where T : ServerMessage;
    Task BroadcastAsync<T>(T message, CancellationToken ct = default)where T : ServerMessage;
}
