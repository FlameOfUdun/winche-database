using Winche.Database.AspNetCore.Rest.Abstraction;
using Winche.Database.AspNetCore.WebSockets.Abstraction;

namespace Winche.Database.Sample.Configurations;

public class RESTClaimsMapper : RestClaimsMapper
{
    public override Task<Dictionary<string, object?>> MapClaims(HttpContext httpContext)
    {
        return Task.FromResult(new Dictionary<string, object?>
        {
            ["uid"] = "123",
        });
    }
}

public class WSClaimsMapper : WsClaimsMapper
{
    public override Task<Dictionary<string, object?>> MapClaims(HttpContext httpContext)
    {
        return Task.FromResult(new Dictionary<string, object?>
        {
            ["uid"] = "123",
        });
    }
}
