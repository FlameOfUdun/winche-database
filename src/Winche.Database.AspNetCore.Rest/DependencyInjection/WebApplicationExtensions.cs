using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Winche.Database.AspNetCore.Rest.EndpointFilters;
using Winche.Database.Documents;
using Winche.Database.Querying;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Ast.Serialization;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

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
    /// <c>:commit</c>/<c>:beginTransaction</c>/<c>:rollback</c>/<c>:batchGet</c>/<c>:runQuery</c>/<c>:count</c>)
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

        // colon-verb operations (extended verb syntax) — built-in claims+error filters always applied.
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
            var writesNode = node["writes"]
                ?? throw new RuntimeException(RuntimeStatus.InvalidArgument, "'writes' is required");
            var transaction = node["transaction"]?.GetValue<string>();
            // JsonException from a malformed write shape propagates to ExceptionHandler → 400 INVALID_ARGUMENT
            var writes = writesNode.Deserialize<IReadOnlyList<Write>>()
                ?? throw new RuntimeException(RuntimeStatus.InvalidArgument, "'writes' must be an array");
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
            var queryNode = node["query"]
                ?? throw new RuntimeException(RuntimeStatus.InvalidArgument, "'query' is required");
            var query = queryNode.Deserialize<Query>(new System.Text.Json.JsonSerializerOptions
            {
                Converters = { new Querying.Ast.Serialization.QueryAstJsonConverter() }
            }) ?? throw new RuntimeException(RuntimeStatus.InvalidArgument, "'query' must be a valid query object");

            var aggregations = ParseAggregations(node["aggregations"]);
            var result = await db.AggregateAsync(query, aggregations, ct);

            var resultObj = new JsonObject();
            foreach (var (alias, value) in result.Values)
                resultObj[alias] = ValueSerializer.Write(value);
            return Results.Json(new { result = resultObj }, statusCode: 200, contentType: "application/json");

            static IReadOnlyList<Aggregation> ParseAggregations(JsonNode? aggNode)
            {
                if (aggNode is null)
                    throw new RuntimeException(RuntimeStatus.InvalidArgument, "'aggregations' is required");
                if (aggNode is not JsonArray arr)
                    throw new RuntimeException(RuntimeStatus.InvalidArgument, "'aggregations' must be an array");
                var list = new List<Aggregation>(arr.Count);
                foreach (var el in arr)
                {
                    if (el is not JsonObject o)
                        throw new RuntimeException(RuntimeStatus.InvalidArgument, "each aggregation must be an object");
                    var kind = (string?)o["kind"] switch
                    {
                        "count" => AggregateKind.Count,
                        "sum" => AggregateKind.Sum,
                        "average" => AggregateKind.Average,
                        _ => throw new RuntimeException(RuntimeStatus.InvalidArgument, "aggregation 'kind' must be count|sum|average"),
                    };
                    var alias = o["alias"] switch
                    {
                        null => throw new RuntimeException(RuntimeStatus.InvalidArgument, "aggregation 'alias' is required"),
                        JsonValue av when av.TryGetValue<string>(out var a) => a,
                        _ => throw new RuntimeException(RuntimeStatus.InvalidArgument, "aggregation 'alias' must be a string"),
                    };
                    var fieldStr = (string?)o["field"];
                    FieldPath? field;
                    try { field = fieldStr is null ? null : FieldPath.Parse(fieldStr); }
                    catch (ArgumentException ex) { throw new RuntimeException(RuntimeStatus.InvalidArgument, ex.Message); }
                    list.Add(new Aggregation(kind, alias, field));
                }
                return list;
            }
        });

        Verb("add", async (HttpRequest request, IDocumentDatabase db, CancellationToken ct) =>
        {
            var node = (await JsonNode.ParseAsync(request.Body, cancellationToken: ct))?.AsObject()
                ?? throw new RuntimeException(RuntimeStatus.InvalidArgument, "Request body must be a JSON object");
            var collection = node["collection"] switch
            {
                null => throw new RuntimeException(RuntimeStatus.InvalidArgument, "'collection' is required"),
                JsonValue cv when cv.TryGetValue<string>(out var cs) => cs,
                _ => throw new RuntimeException(RuntimeStatus.InvalidArgument, "'collection' must be a string"),
            };
            var fieldsNode = node["fields"]
                ?? throw new RuntimeException(RuntimeStatus.InvalidArgument, "'fields' is required");
            var fields = fieldsNode.Deserialize<IReadOnlyDictionary<string, Value>>(
                new System.Text.Json.JsonSerializerOptions
                {
                    Converters = { new Querying.Ast.Serialization.FieldsJsonConverter() }
                })
                ?? throw new RuntimeException(RuntimeStatus.InvalidArgument, "'fields' must be an object");
            var doc = await db.AddAsync(collection, fields, ct);
            return Results.Json(new { document = doc }, statusCode: 200, contentType: "application/json");
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
