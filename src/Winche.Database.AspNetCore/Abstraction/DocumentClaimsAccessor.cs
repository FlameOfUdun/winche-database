using Microsoft.AspNetCore.Http;
using Winche.Database.Authorization;

namespace Winche.Database.AspNetCore.Abstraction;

/// <summary>
/// Base class for HTTP-context-based caller claims accessors.
/// Subclasses implement <see cref="MapClaims"/> to extract claims from the current
/// <see cref="HttpContext"/>. The base class stores claims per async execution context
/// via <see cref="AsyncLocal{T}"/> so that concurrent requests are isolated.
/// <para>
/// REST: the <c>ClaimsAccessor</c> endpoint filter calls <see cref="SetClaims(HttpContext)"/>
/// once per request before the handler runs.<br/>
/// WebSockets: <see cref="ConnectionScope.ApplyClaims()"/> calls <see cref="SetClaims(IReadOnlyDictionary{string,object?})"/>
/// before every database operation so each message loop iteration sees the right claims.
/// </para>
/// </summary>
public abstract class DocumentClaimsAccessor : IRuleClaimsAccessor
{
    private readonly AsyncLocal<IReadOnlyDictionary<string, object?>?> _asyncLocal = new();

    /// <summary>
    /// Maps claims from <paramref name="httpContext"/> into the claims dictionary.
    /// Return <see langword="null"/> to represent an unauthenticated caller.
    /// </summary>
    public abstract IReadOnlyDictionary<string, object?>? MapClaims(HttpContext httpContext);

    /// <summary>
    /// Extracts claims via <see cref="MapClaims"/> and stores them in the current async context.
    /// Called by the REST endpoint filter once per request.
    /// </summary>
    public void SetClaims(HttpContext httpContext) => _asyncLocal.Value = MapClaims(httpContext);

    /// <summary>
    /// Stores <paramref name="claims"/> directly in the current async context.
    /// Called by the WebSocket <c>ConnectionScope</c> before each database operation.
    /// </summary>
    public void SetClaims(IReadOnlyDictionary<string, object?>? claims) => _asyncLocal.Value = claims;

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, object?>? GetClaims() => _asyncLocal.Value;
}
