using Microsoft.AspNetCore.Http;
using System.Text.Json;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Planning;
using Winche.Database.Values;
using Winche.Sentinel.Models;

namespace Winche.Database.AspNetCore.Rest.EndpointFilters;

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
        catch (QueryParseException ex)
        {
            return Results.Json(new { error = ex.Message, code = "invalid_query", path = ex.JsonPath }, statusCode: 400, contentType: "application/json");
        }
        catch (PlanValidationException ex)
        {
            return Results.Json(new { error = ex.Message, code = "invalid_query", detail = ex.Code }, statusCode: 400, contentType: "application/json");
        }
        catch (WireFormatException ex)
        {
            return Results.Json(new { error = ex.Message, code = "invalid_value" }, statusCode: 400, contentType: "application/json");
        }
        catch (JsonException ex)
        {
            return Results.Json(new { error = ex.Message, code = "invalid_request" }, statusCode: 400, contentType: "application/json");
        }
        catch (ArgumentException ex)
        {
            return Results.Json(new { error = ex.Message, code = "invalid_path" }, statusCode: 400, contentType: "application/json");
        }
        catch (Exception ex)
        {
            return Results.Json(new { error = "Unexpected error", detail = ex.Message }, statusCode: 500, contentType: "application/json");
        }
    }
}
