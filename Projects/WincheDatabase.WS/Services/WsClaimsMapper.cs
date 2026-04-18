using Microsoft.AspNetCore.Http;

namespace WincheDatabase.WS.Services;

public sealed class WsClaimsMapper(Func<HttpContext, Dictionary<string, object?>> mapper)
{
    public Dictionary<string, object?> MapClaims(HttpContext httpContext) => mapper(httpContext);
}
