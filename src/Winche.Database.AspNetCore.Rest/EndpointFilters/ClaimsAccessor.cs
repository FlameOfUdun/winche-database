using Microsoft.AspNetCore.Http;
using Winche.Database.AspNetCore.Abstraction;

namespace Winche.Database.AspNetCore.Rest.EndpointFilters;

internal class ClaimsAccessor(
    DocumentClaimsAccessor accessor
) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        accessor.SetClaims(context.HttpContext);
        return await next(context);
    }
}
