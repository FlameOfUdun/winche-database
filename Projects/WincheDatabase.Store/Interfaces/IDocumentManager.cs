using System.Text.Json.Nodes;
using WincheDatabase.AST.Models;
using WincheDatabase.Core.Models;
using WincheDatabase.Store.Models;

namespace WincheDatabase.Store.Interfaces;

public interface IDocumentManager
{
    Task<Document?> GetAsync(string path, CancellationToken ct = default);
    Task<Document> SetAsync(string path, JsonObject data, CancellationToken ct = default);
    Task<Document?> UpdateAsync(string path, JsonObject patch, CancellationToken ct = default);
    Task<bool> DeleteAsync(string path, CancellationToken ct = default);
    Task<QueryResult> QueryAsync(Query query, CancellationToken ct = default);
    Task<AggregateResult> AggregateAsync(AggregationPipeline pipeline, CancellationToken ct = default);
    Task<CommitResult> CommitAsync(OperationBatch batch, CancellationToken ct = default);
    Task<SyncResult> SyncAsync(MutationBatch batch, CancellationToken ct = default);

    Task<Document?> GetUnprotectedAsync(string path, CancellationToken ct = default);
    Task<Document> SetUnprotectedAsync(string path, JsonObject data, CancellationToken ct = default);
    Task<Document?> UpdateUnprotectedAsync(string path, JsonObject patch, CancellationToken ct = default);
    Task<bool> DeleteUnprotectedAsync(string path, CancellationToken ct = default);
    Task<QueryResult> QueryUnprotectedAsync(Query query, CancellationToken ct = default);
    Task<AggregateResult> AggregateUnprotectedAsync(AggregationPipeline pipeline, CancellationToken ct = default);
    Task<CommitResult> CommitUnprotectedAsync(OperationBatch batch, CancellationToken ct = default);
    Task<SyncResult> SyncUnprotectedAsync(string path, List<Mutation> mutations, CancellationToken ct = default);
}
