using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Text;
using System.Text.Json.Nodes;
using Winche.Database.AspNetCore.Rest.EndpointFilters;
using Winche.Database.AST.Models;
using Winche.Database.Interfaces;
using Winche.Database.Models;

namespace Winche.Database.AspNetCore.Rest.DependencyInjection;

public static class WebApplicationExtensions
{
    private static string DecodeBase64(string base64)
    {
        byte[] bytes = Convert.FromBase64String(base64);
        return Encoding.UTF8.GetString(bytes);
    }

    public static WebApplication UseWincheDatabaseRestApi(this WebApplication app, string prefix = "documents", Action<RouteGroupBuilder>? configure = null)
    {
        var group = app.MapGroup(prefix);

        configure?.Invoke(group);

        group.AddEndpointFilter<CallerAccessor>();
        group.AddEndpointFilter<ExceptionHandler>();

        group.MapPut("/{path}", async (string path, JsonObject Data, IDocumentManager manager, CancellationToken ct = default) =>
        {
            var decoded = DecodeBase64(path);
            var document = await manager.SetAsync(decoded, Data, ct);
            return Results.Json(document, statusCode: 200, contentType: "application/json");
        });

        group.MapGet("/{path}", async (string path, IDocumentManager manager, CancellationToken ct = default) =>
        {
            var decoded = DecodeBase64(path);
            var document = await manager.GetAsync(decoded, ct);
            return document is null ? Results.NotFound() : Results.Json(document, statusCode: 200, contentType: "application/json");
        });

        group.MapDelete("/{path}", async (string path, IDocumentManager manager, CancellationToken ct = default) =>
        {
            var decoded = DecodeBase64(path);
            var deleted = await manager.DeleteAsync(decoded, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        });

        group.MapPatch("/{path}", async (string path, JsonObject Data, IDocumentManager manager, CancellationToken ct = default) =>
        {
            var decoded = DecodeBase64(path);
            var document = await manager.UpdateAsync(decoded, Data, ct);
            return document is null ? Results.NotFound() : Results.Json(document, statusCode: 200, contentType: "application/json");
        });

        group.MapPost("/synchronize", async (MutationBatch batch, IDocumentManager manager, CancellationToken ct = default) =>
        {
            var result = await manager.SyncAsync(batch, ct);
            return Results.Json(result, statusCode: 200, contentType: "application/json");
        });

        group.MapPost("/commit", async (OperationBatch batch, IDocumentManager manager, CancellationToken ct = default) =>
        {
            var result = await manager.CommitAsync(batch, ct);
            return Results.Json(result, statusCode: 200, contentType: "application/json");
        });

        group.MapPost("/aggregate", async (AggregationPipeline pipeline, IDocumentManager manager, CancellationToken ct = default) =>
        {
            var result = await manager.AggregateAsync(pipeline, ct);
            return Results.Json(result, statusCode: 200, contentType: "application/json");
        });

        group.MapPost("/query", async (Query query, IDocumentManager manager, CancellationToken ct = default) =>
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