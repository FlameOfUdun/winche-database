using Microsoft.AspNetCore.Http;
using Winche.Database.Authorization;

namespace Winche.Database.AspNetCore.Abstraction;

/// <summary>
/// A database-specific way to read the caller's claims from a request (<c>o.MapClaims(…)</c>), used instead of the
/// host's <c>AddWincheCaller</c> mapping. The claims are kept in Winche.Core's <see cref="WincheCaller"/>, so every
/// package sees the same caller.
/// </summary>
public abstract class DocumentClaimsAccessor : IRuleClaimsAccessor
{
    public abstract IReadOnlyDictionary<string, object?>? MapClaims(HttpContext httpContext);

    public void SetClaims(HttpContext httpContext) => WincheCaller.Set(MapClaims(httpContext));

    public void SetClaims(IReadOnlyDictionary<string, object?>? claims) => WincheCaller.Set(claims);

    public IReadOnlyDictionary<string, object?>? GetClaims() => WincheCaller.Claims;
}
