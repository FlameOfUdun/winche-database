using Microsoft.AspNetCore.Http;

namespace WincheDatabase.REST.Services;

public sealed class RestClaimsMapper(Func<HttpContext, Dictionary<string, object?>> mapper)
{
    public Dictionary<string, object?> MapClaims(HttpContext httpContext) => mapper(httpContext);
}
