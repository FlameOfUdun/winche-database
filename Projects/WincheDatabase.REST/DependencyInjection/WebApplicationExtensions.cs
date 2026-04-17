using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Text;
using System.Text.Json.Nodes;
using WincheDatabase.AST.Models;
using WincheDatabase.Store.Models;
using WincheDatabase.Store.Services;

namespace WincheDatabase.REST.DependencyInjection
{
    public static class WebApplicationExtensions
    {
        private static string DecodeBase64(string base64)
        {
            byte[] bytes = Convert.FromBase64String(base64);
            return Encoding.UTF8.GetString(bytes);
        }

        public static WebApplication UseWincheDatabaseRestApi(
            this WebApplication app,
            string prefix = "documents",
            Func<HttpContext, Task<Dictionary<string, object?>>>? mapClaims = null
        )
        {
            var group = app.MapGroup(prefix);

            group.AddEndpointFilter(async (context, next) =>
            {
                var claims = mapClaims is not null ? await mapClaims(context.HttpContext) : [];
                CallerContext.SetClaims(claims);
                return await next(context);
            });

            group.AddEndpointFilter(async (context, next) =>
            {
                try
                {
                    return await next(context);
                }
                catch (AccessDeniedException ex)
                {
                    return Results.Json(new { error = ex.Message }, statusCode: 403, contentType: "application/json");
                }
                catch (Exception ex)
                {
                    return Results.Json(new { error = "Unexpected error", detail = ex.Message }, statusCode: 500, contentType: "application/json");
                }
            });

            group.MapPut("/{path}", async (string path, JsonObject Data, DocumentManager manager, CancellationToken ct = default) =>
            {
                var decoded = DecodeBase64(path);
                var document = await manager.SetAsync(decoded, Data, ct);
                return Results.Json(document, statusCode: 200, contentType: "application/json");
            });

            group.MapGet("/{path}", async (string path, DocumentManager manager, CancellationToken ct = default) =>
            {
                var decoded = DecodeBase64(path);
                var document = await manager.GetAsync(decoded, ct);
                return document is null ? Results.NotFound() : Results.Json(document, statusCode: 200, contentType: "application/json");
            });

            group.MapDelete("/{path}", async (string path, DocumentManager manager, CancellationToken ct = default) =>
            {
                var decoded = DecodeBase64(path);
                var deleted = await manager.DeleteAsync(decoded, ct);
                return deleted ? Results.NoContent() : Results.NotFound();
            });

            group.MapPatch("/{path}", async (string path, JsonObject Data, DocumentManager manager, CancellationToken ct = default) =>
            {
                var decoded = DecodeBase64(path);
                var document = await manager.UpdateAsync(decoded, Data, ct);
                return document is null ? Results.NotFound() : Results.Json(document, statusCode: 200, contentType: "application/json");
            });

            group.MapPost("/synchronize", async (MutationBatch batch, DocumentManager manager, CancellationToken ct = default) =>
            {
                var result = await manager.SyncAsync(batch, ct);
                return Results.Json(result, statusCode: 200, contentType: "application/json");
            });

            group.MapPost("/commit", async (OperationBatch batch, DocumentManager manager, CancellationToken ct = default) =>
            {
                var result = await manager.CommitAsync(batch, ct);
                return Results.Json(result, statusCode: 200, contentType: "application/json");
            });

            group.MapPost("/aggregate", async (AggregationPipeline pipeline, DocumentManager manager, CancellationToken ct = default) =>
            {
                var result = await manager.AggregateAsync(pipeline, ct);
                return Results.Json(result, statusCode: 200, contentType: "application/json");
            });

            group.MapPost("/query", async (Query query, DocumentManager manager, CancellationToken ct = default) =>
            {
                var result = await manager.QueryAsync(query, ct);
                return Results.Json(result, statusCode: 200, contentType: "application/json");
            });

            group.MapGet("/ping", async (CancellationToken ct = default) =>
            {
                return Results.Ok();
            });

            return app;
        }
    }
}