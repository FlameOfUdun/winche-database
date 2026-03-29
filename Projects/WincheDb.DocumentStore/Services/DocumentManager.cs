using Npgsql;
using System.Collections;
using System.Text.Json.Nodes;
using WincheDb.Core.Ast;
using WincheDb.Core.Models;
using WincheDb.DocumentStore.Infrastructure;
using WincheDb.DocumentStore.Models;
using WincheDb.DocumentStore.Operations;

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
        await AuthorizeAsync(AccessOperation.Read, query.Collection, ct: ct);
        return await QueryUnprotectedAsync(query, ct);
    }

    public async Task<AggregateResult> AggregateAsync(List<PipelineStage> pipeline, CancellationToken ct = default)
    {
        foreach (PipelineStage pipelineStage in pipeline) 
        { 
            if (pipelineStage is MatchStage matchStage)
            {
                await AuthorizeAsync(AccessOperation.Read, matchStage.Collection, ct: ct);
                continue;
            }
            if (pipelineStage is LookupStage lookupStage)
            {
                await AuthorizeAsync(AccessOperation.Read, lookupStage.Collection, ct: ct);
                continue;
            }
        }
        return await AggregateUnprotectedAsync(pipeline, ct);
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

    public async Task<AggregateResult> AggregateUnprotectedAsync(List<PipelineStage> pipeline, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        Console.WriteLine("Executing aggregate operation on table: " + options.TableName);
        return await new AggregateOperation(conn, null, options.TableName)
            .ExecuteAsync(pipeline, ct);
    }

    #endregion

    private async Task AuthorizeAsync(AccessOperation operation, string path, JsonObject? incomingData = null, Func<string, Task<Document?>>? getExisting = null, CancellationToken ct = default)
    {
        await AccessRuleEvaluator.EvaluateAsync(options, operation, path, incomingData, getExisting, ct);
    }
}
