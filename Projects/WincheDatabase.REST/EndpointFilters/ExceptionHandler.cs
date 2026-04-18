using Microsoft.AspNetCore.Http;
using WincheSentinel.Core.Models;

namespace WincheDatabase.REST.EndpointFilters;

internal class ExceptionHandler : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (AccessDeniedException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: 403, contentType: "application/json");
        }
        catch (NoRulesMatchedException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: 403, contentType: "application/json");
        }
        catch (Exception ex)
        {
            return Results.Json(new { error = "Unexpected error", detail = ex.Message }, statusCode: 500, contentType: "application/json");
        }
    }
}
