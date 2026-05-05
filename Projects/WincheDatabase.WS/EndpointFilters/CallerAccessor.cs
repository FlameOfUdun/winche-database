using Microsoft.AspNetCore.Http;
using WincheDatabase.Store.Services;
using WincheDatabase.WS.Abstraction;

namespace WincheDatabase.WS.EndpointFilters;

internal class CallerAccessor(
    WsClaimsMapper mapper,
    CallerContextAccessor accessor
) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var claism = await mapper.MapClaims(context.HttpContext);
        accessor.SetClaims(claism);

        return await next(context);
    }
}
