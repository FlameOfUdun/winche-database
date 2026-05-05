using Microsoft.AspNetCore.Http;
using WincheDatabase.REST.Abstraction;
using WincheDatabase.Store.Services;

namespace WincheDatabase.REST.EndpointFilters;

internal class CallerAccessor(
    RestClaimsMapper mapper,
    CallerContextAccessor accessor
) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var claims = await mapper.MapClaims(context.HttpContext);
        accessor.SetClaims(claims);

        return await next(context);
    }
}
