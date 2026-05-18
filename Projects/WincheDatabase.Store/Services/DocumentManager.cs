using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Text.Json.Nodes;
using WincheDatabase.AST.Models;
using WincheDatabase.Core.Models;
using WincheDatabase.Store.Interfaces;
using WincheDatabase.Store.Constants;
using WincheDatabase.Store.Infrastructure;
using WincheDatabase.Store.Models;
using WincheDatabase.Store.Operations;
using WincheSentinel.Core.Abstraction;
using WincheSentinel.Core.Models;

namespace WincheDatabase.Store.Services;

public sealed class DocumentManager(
    [FromKeyedServices(ServiceKeys.DATA_SOURCE_KEY)] NpgsqlDataSource source,
    IOptions<StoreOptions> options,
    IAccessRuleEvaluator<Document> evaluator,
    HookInvocationDispatcher hookDispatcher,
    ILogger<DocumentManager> logger
) : IDocumentManager
{
    private readonly string _table = options.Value.TableName;

    public async Task<Document?> GetAsync(string path, CancellationToken ct = default)
    {
        await evaluator.EvaluateAsync(AccessOperation.Read, path: path, null, ct);
        return await GetUnprotectedAsync(path, ct); ;
    }

    public async Task<Document> SetAsync(string path, JsonObject data, CancellationToken ct = default)
    {
        await evaluator.EvaluateAsync(AccessOperation.Write, path: path, data, ct);
        return await SetUnprotectedAsync(path, data, ct);
    }

    public async Task<Document?> UpdateAsync(string path, JsonObject patch, CancellationToken ct = default)
    {
        await evaluator.EvaluateAsync(AccessOperation.Write, path, patch, ct);
        return await UpdateUnprotectedAsync(path, patch, ct);
    }

    public async Task<bool> DeleteAsync(string path, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        try
        {
            var op = new DeleteOperation(conn, tx, _table);

            var authorizedPaths = await op.SelectForUpdateAsync(path, ct);
            var authorizedSet = new HashSet<string>(authorizedPaths);

            foreach (var p in authorizedPaths)
                await evaluator.EvaluateAsync(AccessOperation.Delete, p, null, ct);

            var deleted = await op.ExecuteAsync(path, ct);

            var stowaways = deleted.Where(p => !authorizedSet.Contains(p)).ToList();
            if (stowaways.Count > 0)
            {
                await tx.RollbackAsync(ct);
                throw new ConcurrentSubtreeModificationException(path, stowaways);
            }

            await tx.CommitAsync(ct);

            foreach (var p in deleted)
                hookDispatcher.Enqueue(p, (h, t) => h.OnDocumentDeletedAsync(p, t));

            return deleted.Count > 0;
        }
        catch (ConcurrentSubtreeModificationException)
        {
            throw;
        }
        catch
        {
            try { await tx.RollbackAsync(ct); } catch { }
            throw;
        }
    }

    public async Task<QueryResult> QueryAsync(Query query, CancellationToken ct = default)
    {
        await evaluator.EvaluateAsync(AccessOperation.Read, query.Collection, null, ct);
        return await QueryUnprotectedAsync(query, ct);
    }

    public async Task<AggregateResult> AggregateAsync(AggregationPipeline pipeline, CancellationToken ct = default)
    {
        foreach (PipelineStage stage in pipeline.Stages) 
        { 
            if (stage is MatchStage matchStage)
            {
                await evaluator.EvaluateAsync(AccessOperation.Read, matchStage.Collection, null, ct);
                continue;
            }
            if (stage is LookupStage lookupStage)
            {
                await evaluator.EvaluateAsync(AccessOperation.Read, lookupStage.Collection, null, ct);
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
                    await evaluator.EvaluateAsync(AccessOperation.Write, operation.Path, operation.Data, ct);
                    break;
                case BatchOperationType.Update:
                    await evaluator.EvaluateAsync(AccessOperation.Write, operation.Path, operation.Data, ct);
                    break;
                case BatchOperationType.Delete:
                    await evaluator.EvaluateAsync(AccessOperation.Delete, operation.Path, null, ct);
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
                    await evaluator.EvaluateAsync(AccessOperation.Write, batch.Path, mutation.Data, ct);
                    break;

                case MutationType.Update:
                    await evaluator.EvaluateAsync(AccessOperation.Write, batch.Path, mutation.Data, ct);
                    break;

                case MutationType.Delete:
                    await evaluator.EvaluateAsync(AccessOperation.Delete, batch.Path, null, ct); 
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
        var doc = await new SetOperation(conn, null, _table).ExecuteAsync(path, data, ct);
        hookDispatcher.Enqueue(path, (h, t) => h.OnDocumentSetAsync(path, doc, t));
        return doc;
    }

    public async Task<Document?> UpdateUnprotectedAsync(string path, JsonObject patch, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        var doc = await new UpdateOperation(conn, null, _table).ExecuteAsync(path, patch, ct);
        if (doc is not null) hookDispatcher.Enqueue(path, (h, t) => h.OnDocumentUpdatedAsync(path, doc, t));
        return doc;
    }

    public async Task<bool> DeleteUnprotectedAsync(string path, CancellationToken ct = default)
    {
        await using var conn = await source.OpenConnectionAsync(ct);
        var deleted = await new DeleteOperation(conn, null, _table).ExecuteAsync(path, ct);

        foreach (var p in deleted)
            hookDispatcher.Enqueue(p, (h, t) => h.OnDocumentDeletedAsync(p, t));

        return deleted.Count > 0;
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
                var deletedPathsByOp = new List<IReadOnlyList<string>?>(batch.Operations.Count);

                foreach (var operation in batch.Operations)
                {
                    switch (operation.Type)
                    {
                        case BatchOperationType.Set:
                            var setResult = await new SetOperation(conn, tx, _table).ExecuteAsync(operation.Path, operation.Data!, ct);
                            documents.Add(setResult);
                            deletedPathsByOp.Add(null);
                            break;

                        case BatchOperationType.Update:
                            var updateResult = await new UpdateOperation(conn, tx, _table).ExecuteAsync(operation.Path, operation.Data!, ct);
                            documents.Add(updateResult);
                            deletedPathsByOp.Add(null);
                            break;

                        case BatchOperationType.Delete:
                            var deleteResult = await new DeleteOperation(conn, tx, _table).ExecuteAsync(operation.Path, ct);
                            documents.Add(null);
                            deletedPathsByOp.Add(deleteResult);
                            break;
                    }
                }

                await tx.CommitAsync(ct);
                await tx.DisposeAsync();

                for (int i = 0; i < batch.Operations.Count; i++)
                {
                    var op = batch.Operations[i];
                    var doc = documents[i];
                    switch (op.Type)
                    {
                        case BatchOperationType.Set:
                            hookDispatcher.Enqueue(op.Path, (h, t) => h.OnDocumentSetAsync(op.Path, doc!, t));
                            break;
                        case BatchOperationType.Update:
                            if (doc is not null) hookDispatcher.Enqueue(op.Path, (h, t) => h.OnDocumentUpdatedAsync(op.Path, doc, t));
                            break;
                        case BatchOperationType.Delete:
                            var deletedPaths = deletedPathsByOp[i]!;
                            foreach (var p in deletedPaths)
                                hookDispatcher.Enqueue(p, (h, t) => h.OnDocumentDeletedAsync(p, t));
                            break;
                    }
                }

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
        catch (Exception ex)
        {
            logger.LogError(ex, "Commit failed");
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

            var cascadeDeletedPaths = new HashSet<string>();

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
                        var deleted = await new DeleteOperation(conn, tx, _table).ExecuteAsync(path, ct);
                        foreach (var p in deleted)
                            cascadeDeletedPaths.Add(p);
                        current = null;
                        break;
                }

                appliedCount++;
            }

            await tx.CommitAsync(ct);
            await tx.DisposeAsync();

            foreach (var p in cascadeDeletedPaths)
            {
                if (p == path) continue;
                hookDispatcher.Enqueue(p, (h, t) => h.OnDocumentDeletedAsync(p, t));
            }

            if (current is not null)
                hookDispatcher.Enqueue(path, (h, t) => h.OnDocumentSetAsync(path, current, t));
            else if (cascadeDeletedPaths.Contains(path))
                hookDispatcher.Enqueue(path, (h, t) => h.OnDocumentDeletedAsync(path, t));

            return new SyncResult
            {
                Path = path,
                Document = current,
                AppliedCount = appliedCount,
                HasConflict = false,
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sync failed");

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
}
