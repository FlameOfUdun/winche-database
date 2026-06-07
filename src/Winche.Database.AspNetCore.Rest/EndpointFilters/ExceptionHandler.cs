using Microsoft.AspNetCore.Http;
using Winche.Database.Wire;

namespace Winche.Database.AspNetCore.Rest.EndpointFilters;

internal class ExceptionHandler : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (BadHttpRequestException ex)                      // body binding/JSON failures
        {
            return Results.Json(new { status = "INVALID_ARGUMENT", message = ex.Message },
                statusCode: 400, contentType: "application/json");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var error = ErrorMapper.Map(ex);
            var http = error.Status switch
            {
                "NOT_FOUND" => 404,
                "ALREADY_EXISTS" or "ABORTED" => 409,
                "FAILED_PRECONDITION" => 412,
                "PERMISSION_DENIED" => 403,
                "UNAUTHENTICATED" => 401,
                "DEADLINE_EXCEEDED" => 504,
                "INTERNAL" => 500,
                _ => 400,                                       // INVALID_ARGUMENT / INVALID_QUERY
            };
            return Results.Json(new { status = error.Status, message = error.Message, details = error.Details },
                statusCode: http, contentType: "application/json");
        }
    }
}
