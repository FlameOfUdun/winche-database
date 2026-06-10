namespace Winche.Database.Authorization;

/// <summary>
/// Fallback <see cref="IRuleClaimsAccessor"/> that always returns <see langword="null"/>,
/// representing an unauthenticated caller (maps to <c>request.auth == null</c> in rule expressions).
/// Registered first by <c>AddWincheDatabase</c>; transport packages override it by adding a later
/// registration via <c>MapClaims(...)</c>.
/// </summary>
internal sealed class NullRuleClaimsAccessor : IRuleClaimsAccessor
{
    public static readonly NullRuleClaimsAccessor Instance = new();

    public IReadOnlyDictionary<string, object?>? GetClaims() => null;
}
