using WincheDb.DocumentStore.Operations;
using Npgsql;
using System.Text.Json.Nodes;
using WincheDb.DocumentStore.Models;
using WincheDb.Core.Models;
using WincheDb.Core.Ast;
using WincheDb.DocumentStore.Infrastructure;

namespace WincheDb.DocumentStore.Services;

public sealed class DocumentManager(
    NpgsqlDataSource source,
    StoreOptions options
)
{
    public async Task<Document?> GetAsync(string path, CancellationToken ct = default)
    {
        var document = await GetUnprotectedAsync(path, ct);
        await AuthorizeAsync(AccessOperation.Read, path: path, getExisting: _ => Task.FromResult(document), ct: ct);
        return document;
    }

    public async Task<Document> SetAsync(string path, JsonObject data, CancellationToken ct = default)
    {
        await AuthorizeAsync(AccessOperation.Write, path: path, incomingData: data, getExisting: p => GetUnprotectedAsync(p, ct), ct: ct);
        return await SetUnprotectedAsync(path, data, ct);
    }

    public async Task<Document?> UpdateAsync(string path, JsonObject patch, CancellationToken ct = default)
    {
        await AuthorizeAsync(AccessOperation.Write, path: path, incomingData: patch, getExisting: p => GetUnprotectedAsync(p, ct), ct: ct);
        return await UpdateUnprotectedAsync(path, patch, ct);
    }

    public async Task<bool> DeleteAsync(string path, CancellationToken ct = default)
    {
        await AuthorizeAsync(AccessOperation.Delete, path: path, getExisting: p => GetUnprotectedAsync(p, ct), ct: ct);
        return await DeleteUnprotectedAsync(path, ct);
    }

    public async Task<QueryResult> QueryAsync(Query query, CancellationToken ct = default)
    {
        await AuthorizeAsync(AccessOperation.Read, query: query, ct: ct);
        return await QueryUnprotectedAsync(query, ct);
    }

    #region Unprotected — bypass access rules

    public async Task<Document?> GetUnprotectedAsync(string path, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        return await new GetOperation(conn, null, options.TableName)
            .ExecuteAsync(path, ct);
    }

    public async Task<Document> SetUnprotectedAsync(string path, JsonObject data, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        return await new SetOperation(conn, null, options.TableName)
            .ExecuteAsync(path, data, ct);
    }

    public async Task<Document?> UpdateUnprotectedAsync(string path, JsonObject patch, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        return await new UpdateOperation(conn, null, options.TableName)
            .ExecuteAsync(path, patch, ct);
    }

    public async Task<bool> DeleteUnprotectedAsync(string path, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        return await new DeleteOperation(conn, null, options.TableName)
            .ExecuteAsync(path, ct);
    }

    public async Task<QueryResult> QueryUnprotectedAsync(Query query, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        return await new QueryOperation(conn, null, options.TableName)
            .ExecuteAsync(query, ct);
    }

    #endregion

    private async Task AuthorizeAsync(AccessOperation operation, string? path = null, Query? query = null, JsonObject? incomingData = null, Func<string, Task<Document?>>? getExisting = null, CancellationToken ct = default)
    {
        await AccessRuleEvaluator.EvaluateAsync(options, operation, path, query, incomingData, getExisting, ct);
    }
}
