using Winche.Database.Authorization;

namespace Winche.Database.IntegrationTests;

/// <summary>
/// Test <see cref="IRuleClaimsAccessor"/> that returns a fixed claims dictionary (or <see langword="null"/>
/// for an unauthenticated caller). Replaces the old <c>Func&lt;…&gt;</c> claims-provider lambdas after the
/// guard/authorizer switched to direct <see cref="IRuleClaimsAccessor"/> injection.
/// </summary>
internal sealed class StaticClaimsAccessor(IReadOnlyDictionary<string, object?>? claims) : IRuleClaimsAccessor
{
    public IReadOnlyDictionary<string, object?>? GetClaims() => claims;
}
