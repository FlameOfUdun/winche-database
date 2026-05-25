using System.Collections.Immutable;
using Winche.Database.Core.Models;
using WincheSentinel.Interfaces;

namespace Winche.Database.Services;

public sealed class CallerContextAccessor : ICallerContextAccessor<Document>
{
    private readonly AsyncLocal<IReadOnlyDictionary<string, object?>> _asyncLocal = new();

    public IReadOnlyDictionary<string, object?> GetClaims()
    {
        return _asyncLocal.Value ??= ImmutableDictionary<string, object?>.Empty;
    }

    public void SetClaims(Dictionary<string, object?> claims)
    {
        _asyncLocal.Value = claims;
    }

    public void SetClaims(IReadOnlyDictionary<string, object?> claims)
    {
        _asyncLocal.Value = claims;
    }
}
