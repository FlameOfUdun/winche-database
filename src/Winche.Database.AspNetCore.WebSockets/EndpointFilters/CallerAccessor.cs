using Microsoft.AspNetCore.Http;
using Winche.Database.AspNetCore.Abstraction;

namespace Winche.Database.AspNetCore.WebSockets.EndpointFilters;

internal class CallerAccessor(
    DocumentClaimsAccessor accessor
) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        accessor.SetClaims(context.HttpContext);
        return await next(context);
    }
}
