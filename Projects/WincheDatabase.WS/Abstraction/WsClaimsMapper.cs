using Microsoft.AspNetCore.Http;

namespace WincheDatabase.WS.Abstraction;

public abstract class WsClaimsMapper
{
    public abstract Task<Dictionary<string, object?>> MapClaims(HttpContext httpContext);
}
