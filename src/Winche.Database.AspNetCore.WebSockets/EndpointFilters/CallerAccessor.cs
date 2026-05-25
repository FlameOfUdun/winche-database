using Microsoft.AspNetCore.Http;
using Winche.Database.AspNetCore.WebSockets.Abstraction;
using Winche.Database.Services;

namespace Winche.Database.AspNetCore.WebSockets.EndpointFilters;

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
