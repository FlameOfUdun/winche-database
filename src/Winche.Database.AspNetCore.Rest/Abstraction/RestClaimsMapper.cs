using Microsoft.AspNetCore.Http;

namespace Winche.Database.AspNetCore.Rest.Abstraction;

public abstract class RestClaimsMapper
{
    public abstract Task<Dictionary<string, object?>> MapClaims(HttpContext httpContext);
}
