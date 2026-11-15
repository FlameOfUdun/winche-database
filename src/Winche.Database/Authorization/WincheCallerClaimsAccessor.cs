namespace Winche.Database.Authorization;

/// <summary>
/// The default <see cref="IRuleClaimsAccessor"/>: the caller Winche.Core holds for the request
/// (<see cref="WincheCaller"/>, set by the host's <c>UseWincheCaller</c> or the database's <c>MapClaims</c>), or null
/// when signed out or outside a request.
/// </summary>
internal sealed class WincheCallerClaimsAccessor : IRuleClaimsAccessor
{
    public static readonly WincheCallerClaimsAccessor Instance = new();

    public IReadOnlyDictionary<string, object?>? GetClaims() => WincheCaller.Claims;
}
