using Microsoft.AspNetCore.Http;
using WincheDatabase.REST.Services;
using WincheDatabase.Store.Services;

namespace WincheDatabase.REST.EndpointFilters;

internal class CallerAccessor(
    RestClaimsMapper mapper,
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
