using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Winche.Database.AspNetCore.Abstraction;
using Winche.Database.Wire;

namespace Winche.Database.AspNetCore.Rest.EndpointFilters;

internal class ExceptionHandler(ILogger<ExceptionHandler> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (BadHttpRequestException ex)                      // body binding/JSON failures
        {
            var bad = new WincheError(WincheErrorCode.InvalidArgument, ex.Message);
            return Results.Json(bad.ToBody(), statusCode: bad.HttpStatus, contentType: "application/json");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var http0 = context.HttpContext;
            var user = ErrorMapper.UserOf(http0.RequestServices.GetService<DocumentClaimsAccessor>()?.GetClaims());
            var error = ErrorMapper.Map(ex, logger, $"{http0.Request.Method} {http0.Request.Path}", user);
            return Results.Json(error.ToBody(), statusCode: error.HttpStatus, contentType: "application/json");
        }
    }
}
