using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Text;
using System.Text.Json.Serialization;
using Winche.Database.AspNetCore.Rest.EndpointFilters;
using Winche.Database.Querying.Ast;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Writes;
using Winche.Database.Documents;
using Winche.Database.Querying.Ast.Serialization;
using Winche.Database.Values;

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

        group.AddEndpointFilter<ClaimsAccessor>();
        group.AddEndpointFilter<ExceptionHandler>();

        group.MapPut("/{path}", async (string path, DocumentPayload body, IDocumentDatabase db, CancellationToken ct = default) =>
        {
            var decoded = DecodeBase64(path);
            await db.WriteAsync([new SetWrite { Path = decoded, Fields = body.Fields }], ct);
            var doc = await db.GetAsync(decoded, ct);
            return Results.Json(doc, statusCode: 200, contentType: "application/json");
        });

        group.MapGet("/{path}", async (string path, IDocumentDatabase db, CancellationToken ct = default) =>
        {
            var decoded = DecodeBase64(path);
            var document = await db.GetAsync(decoded, ct);
            return document is null ? Results.NotFound() : Results.Json(document, statusCode: 200, contentType: "application/json");
        });

        group.MapDelete("/{path}", async (string path, IDocumentDatabase db, CancellationToken ct = default) =>
        {
            var decoded = DecodeBase64(path);
            if (await db.GetAsync(decoded, ct) is null) return Results.NotFound();
            await db.WriteAsync([new DeleteWrite { Path = decoded, Cascade = true }], ct);
            return Results.NoContent();
        });

        group.MapPatch("/{path}", async (string path, DocumentPayload body, IDocumentDatabase db, CancellationToken ct = default) =>
        {
            var decoded = DecodeBase64(path);
            var updateFields = body.Fields.ToDictionary(
                kv => FieldPath.Parse(kv.Key),
                kv => kv.Value);
            await db.WriteAsync([new UpdateWrite { Path = decoded, Fields = updateFields }], ct);
            var doc = await db.GetAsync(decoded, ct);
            return doc is null ? Results.NotFound() : Results.Json(doc, statusCode: 200, contentType: "application/json");
        });

        group.MapPost("/aggregate", async (PipelineAst pipeline, IDocumentDatabase db, CancellationToken ct = default) =>
        {
            var result = await db.AggregateAsync(pipeline, ct);
            return Results.Json(result, statusCode: 200, contentType: "application/json");
        });

        group.MapPost("/query", async (QueryAst query, IDocumentDatabase db, CancellationToken ct = default) =>
        {
            var result = await db.QueryAsync(query, ct);
            return Results.Json(result, statusCode: 200, contentType: "application/json");
        });

        group.MapGet("/ping", async (CancellationToken ct = default) =>
        {
            return Results.Ok();
        });

        return app;
    }
}

public sealed record DocumentPayload
{
    [JsonPropertyName("fields")]
    [JsonConverter(typeof(FieldsJsonConverter))]
    public required IReadOnlyDictionary<string, Value> Fields { get; init; }
}
