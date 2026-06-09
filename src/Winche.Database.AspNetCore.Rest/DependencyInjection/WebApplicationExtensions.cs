using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Winche.Database.AspNetCore.Rest.EndpointFilters;
using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Ast.Serialization;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;
using Winche.Database.Wire;

namespace Winche.Database.AspNetCore.Rest.DependencyInjection;

public static class WebApplicationExtensions
{
    private static string DecodeBase64(string base64)
    {
        byte[] bytes = Convert.FromBase64String(base64);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Maps the REST surface (CRUD + ping under <c>/{prefix}</c>, plus the colon-verb operations
    /// <c>:commit</c>/<c>:beginTransaction</c>/<c>:rollback</c>/<c>:batchGet</c>/<c>:runQuery</c>/<c>:count</c>/<c>:aggregate</c>)
    /// and returns a single <see cref="IEndpointConventionBuilder"/> covering ALL of them. Apply
    /// cross-cutting policy on it — e.g. <c>.RequireAuthorization()</c>, rate limiting, CORS — and it
    /// lands on every endpoint including the verbs. The built-in claims/exception filters are always
    /// applied internally and run outermost regardless.
    /// </summary>
    public static IEndpointConventionBuilder MapWincheDatabaseRestApi(this WebApplication app, string prefix = "documents")
    {
        var p = prefix.TrimStart('/');
        var group = app.MapGroup($"/{p}");

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

        group.MapGet("/ping", async (CancellationToken ct = default) =>
        {
            return Results.Ok();
        });

        // colon-verb operations (Firestore dialect) — built-in claims+error filters always applied.
        // Each verb is a standalone route (mapped on `app`, not under the group, because of the ':'),
        // so collect them and fold them into the returned composite builder alongside the group.
        var verbs = new List<IEndpointConventionBuilder>();
        RouteHandlerBuilder Verb(string verb, Delegate handler)
        {
            var builder = app.MapPost($"/{p}:{verb}", handler)
               .AddEndpointFilter<ClaimsAccessor>()
               .AddEndpointFilter<ExceptionHandler>();
            verbs.Add(builder);
            return builder;
        }

        Verb("commit", async (HttpRequest request, IDocumentDatabase db, CancellationToken ct) =>
        {
            var node = (await JsonNode.ParseAsync(request.Body, cancellationToken: ct))?.AsObject()
                ?? throw new RuntimeException(RuntimeStatus.InvalidArgument, "Request body must be a JSON object");
            var writesArray = node["writes"] as JsonArray
                ?? throw new RuntimeException(RuntimeStatus.InvalidArgument, "'writes' is required");
            var transaction = node["transaction"]?.GetValue<string>();
            var writes = WriteWireParser.Parse(writesArray);
            var results = transaction is null
                ? await db.WriteAsync(writes, ct)
                : await db.CommitTransactionAsync(transaction, writes, ct);
            return Results.Json(new { writeResults = results }, statusCode: 200, contentType: "application/json");
        });

        Verb("beginTransaction", async (HttpRequest request, IDocumentDatabase db, CancellationToken ct) =>
        {
            // body is optional / may be empty — tolerate both absent and present JSON objects
            if (request.ContentLength is > 0 || request.Headers.ContentType.Count > 0)
            {
                try { await JsonNode.ParseAsync(request.Body, cancellationToken: ct); }
                catch (System.Text.Json.JsonException) { /* empty or whitespace-only body — ignore */ }
            }
            var handle = await db.BeginTransactionAsync(ct);
            return Results.Json(new { transaction = handle.Id }, statusCode: 200, contentType: "application/json");
        });

        Verb("rollback", async (HttpRequest request, IDocumentDatabase db, CancellationToken ct) =>
        {
            var node = (await JsonNode.ParseAsync(request.Body, cancellationToken: ct))?.AsObject()
                ?? throw new RuntimeException(RuntimeStatus.InvalidArgument, "Request body must be a JSON object");
            var transaction = node["transaction"]?.GetValue<string>()
                ?? throw new RuntimeException(RuntimeStatus.InvalidArgument, "'transaction' is required");
            await db.RollbackTransactionAsync(transaction, ct);     // idempotent; unknown id is a no-op
            return Results.Json(new { }, statusCode: 200, contentType: "application/json");
        });

        Verb("batchGet", async (HttpRequest request, IDocumentDatabase db, CancellationToken ct) =>
        {
            var node = (await JsonNode.ParseAsync(request.Body, cancellationToken: ct))?.AsObject()
                ?? throw new RuntimeException(RuntimeStatus.InvalidArgument, "Request body must be a JSON object");
            var docsArray = node["documents"] as JsonArray
                ?? throw new RuntimeException(RuntimeStatus.InvalidArgument, "'documents' is required");
            var documents = docsArray.Select(n => n?.GetValue<string>()
                ?? throw new RuntimeException(RuntimeStatus.InvalidArgument, "Each document path must be a string"))
                .ToList();
            var transaction = node["transaction"]?.GetValue<string>();
            IReadOnlyList<Document?> docs;
            if (transaction is null)
            {
                docs = await db.GetAllAsync(documents, ct);
            }
            else
            {
                var list = new List<Document?>(documents.Count);
                foreach (var docPath in documents)
                    list.Add(await db.GetAsync(transaction, docPath, ct));  // recorded reads
                docs = list;
            }
            return Results.Json(new { documents = docs }, statusCode: 200, contentType: "application/json");
        });

        Verb("runQuery", async (HttpRequest request, IDocumentDatabase db, CancellationToken ct) =>
        {
            var node = (await JsonNode.ParseAsync(request.Body, cancellationToken: ct))?.AsObject()
                ?? throw new RuntimeException(RuntimeStatus.InvalidArgument, "Request body must be a JSON object");
            var queryNode = node["query"]
                ?? throw new RuntimeException(RuntimeStatus.InvalidArgument, "'query' is required");
            var query = queryNode.Deserialize<Query>(new System.Text.Json.JsonSerializerOptions
            {
                Converters = { new Querying.Ast.Serialization.QueryAstJsonConverter() }
            });
            if (query is null) throw new RuntimeException(RuntimeStatus.InvalidArgument, "'query' must be a valid query object");
            var transaction = node["transaction"]?.GetValue<string>();
            var result = transaction is null
                ? await db.QueryAsync(query, ct)
                : await db.QueryAsync(transaction, query, ct);
            return Results.Json(result, statusCode: 200, contentType: "application/json");
        });

        Verb("count", async (HttpRequest request, IDocumentDatabase db, CancellationToken ct) =>
        {
            var node = (await JsonNode.ParseAsync(request.Body, cancellationToken: ct))?.AsObject()
                ?? throw new RuntimeException(RuntimeStatus.InvalidArgument, "Request body must be a JSON object");
            var queryNode = node["query"]
                ?? throw new RuntimeException(RuntimeStatus.InvalidArgument, "'query' is required");
            var query = queryNode.Deserialize<Query>(new System.Text.Json.JsonSerializerOptions
            {
                Converters = { new Querying.Ast.Serialization.QueryAstJsonConverter() }
            });
            if (query is null) throw new RuntimeException(RuntimeStatus.InvalidArgument, "'query' must be a valid query object");
            var count = await db.CountAsync(query, ct);
            return Results.Json(new { count }, statusCode: 200, contentType: "application/json");
        });

        Verb("aggregate", async (HttpRequest request, IDocumentDatabase db, CancellationToken ct) =>
        {
            var node = (await JsonNode.ParseAsync(request.Body, cancellationToken: ct))?.AsObject()
                ?? throw new RuntimeException(RuntimeStatus.InvalidArgument, "Request body must be a JSON object");
            var pipelineNode = node["pipeline"]
                ?? throw new RuntimeException(RuntimeStatus.InvalidArgument, "'pipeline' is required");
            var pipeline = pipelineNode.Deserialize<Pipeline>(new System.Text.Json.JsonSerializerOptions
            {
                Converters = { new Querying.Ast.Serialization.PipelineAstJsonConverter() }
            });
            if (pipeline is null) throw new RuntimeException(RuntimeStatus.InvalidArgument, "'pipeline' must be a valid pipeline object");
            var result = await db.AggregateAsync(pipeline, ct);
            return Results.Json(result, statusCode: 200, contentType: "application/json");
        });

        return new CompositeEndpointConventionBuilder([group, .. verbs]);
    }
}

public sealed record DocumentPayload
{
    [JsonPropertyName("fields")]
    [JsonConverter(typeof(FieldsJsonConverter))]
    public required IReadOnlyDictionary<string, Value> Fields { get; init; }
}
