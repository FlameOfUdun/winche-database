using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Winche.Database.AspNetCore.WebSockets.Connections;

/// <summary>
/// Tracks open WebSocket connections by user, so access can be withdrawn from a connection that is already
/// open. A connection is authenticated once, at the upgrade, and otherwise keeps that identity for its
/// whole life: a user deleted or disabled at the identity provider would keep reading and writing until the
/// socket happened to drop. <see cref="CloseUserConnectionsAsync"/> closes them (close code 4401); a client
/// that is still entitled reconnects with a fresh token, one that isn't can't.
/// <para>
/// Works across nodes: the call closes the user's connections here and sends a Postgres notification that
/// every other node acts on. A node that is reconnecting its listener at that moment misses it.
/// </para>
/// </summary>
public sealed class WsConnectionRegistry(NpgsqlDataSource source, ILogger<WsConnectionRegistry> logger)
{
    /// <summary>Postgres notification channel used to reach the other nodes.</summary>
    public const string NotifyChannel = "winche_ws_revoke";

    /// <summary>Close code sent to revoked connections: re-authenticate.</summary>
    public const int RevokedCloseCode = 4401;

    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<long, Func<string, Task>>> _byUser = new();
    private long _next;

    /// <summary>Open connections on this node for <paramref name="userId"/>.</summary>
    public int LocalConnectionCount(string userId) => _byUser.TryGetValue(userId, out var c) ? c.Count : 0;

    /// <summary>
    /// Closes every connection <paramref name="userId"/> has open, on every node. Call it when the user is
    /// deleted, disabled or signed out everywhere.
    /// </summary>
    public async Task CloseUserConnectionsAsync(string userId, string reason = "access revoked", CancellationToken ct = default)
    {
        await CloseLocalAsync(userId, reason);
        await using var command = source.CreateCommand("SELECT pg_notify(@channel, @payload)");
        command.Parameters.AddWithValue("channel", NotifyChannel);
        command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(new Revocation(userId, reason)));
        await command.ExecuteNonQueryAsync(ct);
    }

    internal IDisposable Register(string userId, Func<string, Task> close)
    {
        var id = Interlocked.Increment(ref _next);
        _byUser.GetOrAdd(userId, _ => new()).TryAdd(id, close);
        return new Registration(() =>
        {
            if (!_byUser.TryGetValue(userId, out var connections)) return;
            connections.TryRemove(id, out _);
            if (connections.IsEmpty) _byUser.TryRemove(new KeyValuePair<string, ConcurrentDictionary<long, Func<string, Task>>>(userId, connections));
        });
    }

    internal async Task<int> CloseLocalAsync(string userId, string reason)
    {
        if (!_byUser.TryGetValue(userId, out var connections)) return 0;
        var closing = connections.Values.Select(async close =>
        {
            try { await close(reason).WaitAsync(CloseTimeout); }
            catch (Exception ex) { logger.LogWarning(ex, "Closing a revoked WebSocket connection did not finish cleanly"); }
        }).ToList();
        await Task.WhenAll(closing);
        if (closing.Count > 0) logger.LogInformation("Closed {Count} WebSocket connection(s) for a revoked user", closing.Count);
        return closing.Count;
    }

    internal async Task HandleNotificationAsync(string payload)
    {
        Revocation? revocation;
        try { revocation = JsonSerializer.Deserialize<Revocation>(payload); }
        catch (JsonException) { return; }
        if (revocation?.UserId is { } userId) await CloseLocalAsync(userId, revocation.Reason ?? "access revoked");
    }

    private sealed record Revocation(string UserId, string? Reason);

    private sealed class Registration(Action dispose) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) dispose();
        }
    }
}
