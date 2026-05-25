using System.Collections.Concurrent;
using Winche.Database.AspNetCore.WebSockets.Interfaces;
using Winche.Database.AspNetCore.WebSockets.Messages;
using Winche.Database.AspNetCore.WebSockets.Operands;

namespace Winche.Database.AspNetCore.WebSockets.Services;

public sealed class ConnectionRegistry : IConnectionRegistry
{
    private readonly ConcurrentDictionary<string, WebSocketConnection> _connections = new(StringComparer.Ordinal);

    public void Add(string connectionId, WebSocketConnection connection)
    {
        _connections.TryAdd(connectionId, connection);
    }

    public bool TryGet(string connectionId, out WebSocketConnection? connection)
    {
        return _connections.TryGetValue(connectionId, out connection);
    }

    public void Remove(string connectionId)
    {
        _connections.TryRemove(connectionId, out _);
    }

    public int Count => _connections.Count;

    public async Task SendAsync<T>(string connectionId, T message, CancellationToken ct = default) where T : ServerMessage
    {
        if (!_connections.TryGetValue(connectionId, out var connection))
            return;

        if (!connection.IsOpen)
        {
            _connections.TryRemove(connectionId, out _);
            return;
        }

        try
        {
            await connection.SendAsync(message, ct);
        }
        catch (Exception)
        {
            _connections.TryRemove(connectionId, out _);
        }
    }

    public async Task SendBatchAsync<T>(string connectionId, IReadOnlyList<T> messages, CancellationToken ct = default) where T : ServerMessage
    {
        if (messages.Count == 0)
            return;

        if (!_connections.TryGetValue(connectionId, out var connection))
            return;

        if (!connection.IsOpen)
        {
            _connections.TryRemove(connectionId, out _);
            return;
        }

        try
        {
            await connection.SendBatchAsync(messages, ct);
        }
        catch (Exception)
        {
            _connections.TryRemove(connectionId, out _);
        }
    }

    public async Task BroadcastAsync<T>(T message, CancellationToken ct = default) where T : ServerMessage
    {
        var tasks = _connections.Values
            .Where(c => c.IsOpen)
            .Select(c => c.SendAsync(message, ct));

        await Task.WhenAll(tasks);
    }
}
