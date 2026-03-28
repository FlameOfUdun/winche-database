using WincheDb.Core.Models;
using WincheDb.DocumentStore.Services;
using WincheDb.Realtime.Messages;

namespace WincheDb.Realtime.Handlers;

internal sealed class BatchCommitHandler(TransactionManager transactionManager) : IMessageHandler<BatchCommitRequest>
{
    public async Task<ServerMessage> HandleAsync(string connectionId, BatchCommitRequest request, CancellationToken ct)
    {
        var tx = await transactionManager.BeginAsync(ct);

        try
        {
            var documents = new List<Document?>(request.Operations.Count);

            foreach (var op in request.Operations)
            {
                var path = $"{op.Collection}/{op.DocumentId}";

                switch (op.Type)
                {
                    case "set":
                        var setResult = await tx.SetAsync(path, op.Data!, ct);
                        documents.Add(setResult);
                        break;

                    case "update":
                        var updateResult = await tx.UpdateAsync(path, op.Data!, ct);
                        documents.Add(updateResult);
                        break;

                    case "delete":
                        await tx.DeleteAsync(path, ct);
                        documents.Add(null);
                        break;

                    default:
                        await tx.RollbackAsync(ct);
                        await tx.DisposeAsync();
                        return new SystemErrorResponse
                        {
                            RequestId = request.Id,
                            Code = "invalid_batch_operation",
                            Message = $"Unknown batch operation type: {op.Type}"
                        };
                }
            }

            await tx.CommitAsync(ct);
            await tx.DisposeAsync();

            return new BatchCommitResponse
            {
                RequestId = request.Id,
                Documents = documents
            };
        }
        catch
        {
            await tx.DisposeAsync();
            throw;
        }
    }
}
