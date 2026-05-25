using Microsoft.AspNetCore.Http;

namespace Winche.Database.AspNetCore.WebSockets.Abstraction;

public abstract class WsClaimsMapper
{
    public abstract Task<Dictionary<string, object?>> MapClaims(HttpContext httpContext);
}
