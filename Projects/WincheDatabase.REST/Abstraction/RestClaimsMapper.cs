using Microsoft.AspNetCore.Http;

namespace WincheDatabase.REST.Abstraction;

public abstract class RestClaimsMapper
{
    public abstract Task<Dictionary<string, object?>> MapClaims(HttpContext httpContext);
}
