namespace Winche.Database.Authorization;

/// <summary>
/// Provides caller claims for the Winche.Rules-based authorization guard
/// (<see cref="RuleGuardedDocumentDatabase"/>).
/// Implementations bridge whatever auth source is available (HTTP context via
/// <see cref="Winche.Database.AspNetCore.Abstraction.DocumentClaimsAccessor"/>, test fixtures,
/// etc.) into the <c>Func&lt;IReadOnlyDictionary&lt;string,object?&gt;?&gt;</c> the guard expects.
/// Returns <see langword="null"/> when no authenticated caller is present (unauthenticated request).
/// </summary>
public interface IRuleClaimsAccessor
{
    /// <summary>
    /// Retrieves the current caller's claims dictionary, or <see langword="null"/> if the
    /// caller is unauthenticated (maps to an absent <c>request.auth</c> in rule expressions).
    /// </summary>
    IReadOnlyDictionary<string, object?>? GetClaims();
}
