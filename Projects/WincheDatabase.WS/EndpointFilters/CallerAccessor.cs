using Microsoft.AspNetCore.Http;
using WincheDatabase.WS.Services;
using WincheDatabase.Store.Services;

namespace WincheDatabase.WS.EndpointFilters;

internal class CallerAccessor(
    WsClaimsMapper mapper,
    CallerContextAccessor accessor
) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var claism = mapper.MapClaims(context.HttpContext);
        accessor.SetClaims(claism);

        return await next(context);
    }
}
