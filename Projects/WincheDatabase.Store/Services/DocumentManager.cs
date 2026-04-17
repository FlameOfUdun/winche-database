using Microsoft.Extensions.Options;
using Npgsql;
using System.Text.Json.Nodes;
using WincheDatabase.AST.Models;
using WincheDatabase.Core.Models;
using WincheDatabase.Store.Operations;
using WincheDatabase.Store.Models;

namespace WincheDatabase.Store.Services;

public sealed class DocumentManager(NpgsqlDataSource source, IOptions<StoreOptions> options, AccessRuleEvaluator evaluator)
{
    private readonly string _table = options.Value.TableName;

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

    public async Task<AggregateResult> AggregateAsync(AggregationPipeline pipeline, CancellationToken ct = default)
    {
        foreach (PipelineStage stage in pipeline.Stages) 
        { 
            if (stage is MatchStage matchStage)
            {
                await AuthorizeAsync(AccessOperation.Read, matchStage.Collection, ct: ct);
                continue;
            }
            if (stage is LookupStage lookupStage)
            {
                await AuthorizeAsync(AccessOperation.Read, lookupStage.Collection, ct: ct);
                continue;
            }
        }
        return await AggregateUnprotectedAsync(pipeline, ct);
    }

    public async Task<CommitResult> CommitAsync(OperationBatch batch, CancellationToken ct = default)
    {
        foreach (BatchOperation operation in batch.Operations) 
        { 
            switch (operation.Type)
            {
                case BatchOperationType.Set:
                    await AuthorizeAsync(AccessOperation.Write, operation.Path, operation.Data, p => GetUnprotectedAsync(p, ct), ct);
                    break;
                case BatchOperationType.Update:
                    await AuthorizeAsync(AccessOperation.Write, operation.Path, operation.Data, p => GetUnprotectedAsync(p, ct), ct);
                    break;
                case BatchOperationType.Delete:
                    await AuthorizeAsync(AccessOperation.Delete, operation.Path, getExisting: p => GetUnprotectedAsync(p, ct), ct: ct);
                    break;
            }
        }

        return await CommitUnprotectedAsync(batch, ct);
    }

    public async Task<SyncResult> SyncAsync(MutationBatch batch, CancellationToken ct = default)
    {
        foreach (var mutation in batch.Mutations)
        {
            switch (mutation.Type)
            {
                case MutationType.Set:
                    await AuthorizeAsync(AccessOperation.Write, batch.Path, mutation.Data, p => GetUnprotectedAsync(p, ct), ct);
                    break;

                case MutationType.Update:
                    await AuthorizeAsync(AccessOperation.Write, batch.Path, mutation.Data, p => GetUnprotectedAsync(p, ct), ct);
                    break;

                case MutationType.Delete:
                    await AuthorizeAsync(AccessOperation.Delete, batch.Path, getExisting: p => GetUnprotectedAsync(p, ct), ct: ct); 
                    break;
            }
        }

        return await SyncUnprotectedAsync(batch.Path, batch.Mutations, ct);
    }

    #region Unprotected — bypass access rules

    public async Task<Document?> GetUnprotectedAsync(string path, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        return await new GetOperation(conn, null, _table)
            .ExecuteAsync(path, ct);
    }

    public async Task<Document> SetUnprotectedAsync(string path, JsonObject data, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        return await new SetOperation(conn, null, _table)
            .ExecuteAsync(path, data, ct);
    }

    public async Task<Document?> UpdateUnprotectedAsync(string path, JsonObject patch, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        return await new UpdateOperation(conn, null, _table)
            .ExecuteAsync(path, patch, ct);
    }

    public async Task<bool> DeleteUnprotectedAsync(string path, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        return await new DeleteOperation(conn, null, _table)
            .ExecuteAsync(path, ct);
    }

    public async Task<QueryResult> QueryUnprotectedAsync(Query query, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        return await new QueryOperation(conn, null, _table)
            .ExecuteAsync(query, ct);
    }

    public async Task<AggregateResult> AggregateUnprotectedAsync(AggregationPipeline pipeline, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        return await new AggregateOperation(conn, null, _table).ExecuteAsync(pipeline, ct);
    }

    public async Task<CommitResult> CommitUnprotectedAsync(OperationBatch batch, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            try
            {
                var documents = new List<Document?>(batch.Operations.Count);

                foreach (var operation in batch.Operations)
                {
                    switch (operation.Type)
                    {
                        case BatchOperationType.Set:
                            var setResult = await new SetOperation(conn, tx, _table).ExecuteAsync(operation.Path, operation.Data!, ct);
                            documents.Add(setResult);
                            break;

                        case BatchOperationType.Update:
                            var updateResult = await new UpdateOperation(conn, tx, _table).ExecuteAsync(operation.Path, operation.Data!, ct);
                            documents.Add(updateResult);
                            break;

                        case BatchOperationType.Delete:
                            var deleteResult = await new DeleteOperation(conn, tx, _table).ExecuteAsync(operation.Path, ct);
                            documents.Add(null);
                            break;
                    }
                }

                await tx.CommitAsync(ct);
                await tx.DisposeAsync();

                return new CommitResult
                {
                    Documents = documents,
                };
            }
            catch
            {
                await tx.RollbackAsync(ct);
                await tx.DisposeAsync();
                throw;
            }
        }
        catch
        {
            await tx.DisposeAsync();
            throw;
        }
    }

    public async Task<SyncResult> SyncUnprotectedAsync(string path, List<Mutation> mutations, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var appliedCount = 0;

        try
        {
            var current = await new GetOperation(conn, tx, _table).ExecuteAsync(path, ct);

            var firstMutation = mutations[0];
            if (firstMutation.BaseVersion.HasValue
                && current != null
                && current.Version != firstMutation.BaseVersion.Value)
            {
                await tx.RollbackAsync(ct);
                await tx.DisposeAsync();

                return new SyncResult
                {
                    Path = path,
                    Document = current,
                    AppliedCount = 0,
                    HasConflict = true,
                };
            }

            foreach (var mutation in mutations)
            {
                switch (mutation.Type)
                {
                    case MutationType.Set:
                        current = await new SetOperation(conn, tx, _table).ExecuteAsync(path, mutation.Data!, ct);
                        break;

                    case MutationType.Update:
                        current = await new UpdateOperation(conn, tx, _table).ExecuteAsync(path, mutation.Data!, ct);
                        break;

                    case MutationType.Delete:
                        await new DeleteOperation(conn, tx, _table).ExecuteAsync(path, ct);
                        current = null;
                        break;
                }

                appliedCount++;
            }

            await tx.CommitAsync(ct);
            await tx.DisposeAsync();

            return new SyncResult
            {
                Path = path,
                Document = current,
                AppliedCount = appliedCount,
                HasConflict = false,
            };
        }
        catch
        {
            await tx.DisposeAsync();

            var serverDoc = await new GetOperation(conn, null, _table).ExecuteAsync(path, ct);
            return new SyncResult
            {
                Path = path,
                Document = serverDoc,
                AppliedCount = appliedCount,
                HasConflict = true,
            };
        }
    }

    #endregion

    private async Task AuthorizeAsync(AccessOperation operation, string path, JsonObject? incomingData = null, Func<string, Task<Document?>>? getExisting = null, CancellationToken ct = default)
    {
        await evaluator.EvaluateAsync(operation, path, incomingData, getExisting, ct);
    }
}
