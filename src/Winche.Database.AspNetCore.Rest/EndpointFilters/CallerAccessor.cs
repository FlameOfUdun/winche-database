using Microsoft.AspNetCore.Http;
using Winche.Database.AspNetCore.Rest.Abstraction;
using Winche.Database.Services;

namespace Winche.Database.AspNetCore.Rest.EndpointFilters;

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
