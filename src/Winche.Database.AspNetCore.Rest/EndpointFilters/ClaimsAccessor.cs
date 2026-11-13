using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Winche.Database.AspNetCore.Abstraction;

namespace Winche.Database.AspNetCore.Rest.EndpointFilters;

/// <summary>Sets the caller for the request: the database's own MapClaims when it has one, else the host's.</summary>
internal class ClaimsAccessor : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var own = http.RequestServices.GetService<DocumentClaimsAccessor>();
        WincheCaller.Set(await http.WincheCallerOf(own is null ? null : own.MapClaims));
        return await next(context);
    }
}
