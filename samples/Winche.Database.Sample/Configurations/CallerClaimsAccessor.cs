using System.Collections.Immutable;
using Winche.Database.AspNetCore.Abstraction;

namespace Winche.Database.Sample.Configurations;

public class CallerClaimsAccessor : DocumentClaimsAccessor
{
    public override IReadOnlyDictionary<string, object?> MapClaims(HttpContext httpContext)
    {
        // Extract claims from the HttpContext and store them for access rule evaluation.
        // Example: read from JWT claims, headers, or session.
        return ImmutableDictionary<string, object?>.Empty;
    }
}
