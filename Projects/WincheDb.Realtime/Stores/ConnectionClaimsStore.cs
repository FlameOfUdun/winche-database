using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace WincheDb.Realtime.Stores;

public sealed class ConnectionClaimsStore
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
