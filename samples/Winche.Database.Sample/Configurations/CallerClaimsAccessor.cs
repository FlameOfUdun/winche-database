using System.Collections.Immutable;
using Winche.Database.AspNetCore.Abstraction;

namespace Winche.Database.Sample.Configurations;

public class CallerClaimsAccessor : DocumentClaimsAccessor
{
    public override IReadOnlyDictionary<string, object?> MapClaims(HttpContext httpContext)
    {
        return new Dictionary<string, object?>() 
        {
            ["uid"] = "user-123"
        };
    }
}
