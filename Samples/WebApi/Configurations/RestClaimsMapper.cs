using WincheDatabase.REST.Abstraction;
using WincheDatabase.WS.Abstraction;

namespace WincheDatabase.Sample.Configurations;

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
