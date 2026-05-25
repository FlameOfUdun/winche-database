using System.Collections.Concurrent;
using System.Collections.Immutable;
using Winche.Database.AspNetCore.WebSockets.Interfaces;

namespace Winche.Database.AspNetCore.WebSockets.Services;

public sealed class ConnectionClaimsStore : IConnectionClaimsStore
{
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, object?>> _claims = new(StringComparer.Ordinal);

    public void SetClaims(string connectionId, IReadOnlyDictionary<string, object?> claims)
    {
        _claims[connectionId] = claims;
    }

    public IReadOnlyDictionary<string, object?> GetClaims(string connectionId)
    {
        return _claims.TryGetValue(connectionId, out var claims)
            ? claims
            : ImmutableDictionary<string, object?>.Empty;
    }

    public void Remove(string connectionId)
    {
        _claims.TryRemove(connectionId, out _);
    }
}
