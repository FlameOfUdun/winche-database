using Winche.Database.Documents;
using Winche.Database.Models;
using Winche.Database.Querying;
using Winche.Database.Querying.Ast;
using Winche.Database.Values;

namespace Winche.Database.Interfaces;

public interface IDocumentManager
{
    Task<Document?> GetAsync(string path, CancellationToken ct = default);
    Task<Document> SetAsync(string path, IReadOnlyDictionary<string, Value> fields, CancellationToken ct = default);
    Task<Document?> UpdateAsync(string path, IReadOnlyDictionary<string, Value> patch, CancellationToken ct = default);
    Task<bool> DeleteAsync(string path, CancellationToken ct = default);
    Task<QueryResult> QueryAsync(QueryAst query, CancellationToken ct = default);
    Task<PipelineResult> AggregateAsync(PipelineAst pipeline, CancellationToken ct = default);
    Task<CommitResult> CommitAsync(OperationBatch batch, CancellationToken ct = default);
    Task<SyncResult> SyncAsync(MutationBatch batch, CancellationToken ct = default);

    Task<Document?> GetUnprotectedAsync(string path, CancellationToken ct = default);
    Task<Document> SetUnprotectedAsync(string path, IReadOnlyDictionary<string, Value> fields, CancellationToken ct = default);
    Task<Document?> UpdateUnprotectedAsync(string path, IReadOnlyDictionary<string, Value> patch, CancellationToken ct = default);
    Task<bool> DeleteUnprotectedAsync(string path, CancellationToken ct = default);
    Task<QueryResult> QueryUnprotectedAsync(QueryAst query, CancellationToken ct = default);
    Task<PipelineResult> AggregateUnprotectedAsync(PipelineAst pipeline, CancellationToken ct = default);
    Task<CommitResult> CommitUnprotectedAsync(OperationBatch batch, CancellationToken ct = default);
    Task<SyncResult> SyncUnprotectedAsync(string path, List<Mutation> mutations, CancellationToken ct = default);
}
