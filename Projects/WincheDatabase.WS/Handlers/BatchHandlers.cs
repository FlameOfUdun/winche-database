using WincheDatabase.Store.Abstraction;
using WincheDatabase.WS.Messages;

namespace WincheDatabase.WS.Handlers;

internal sealed class BatchCommitHandler(
    IDocumentManager documentManager
) : IMessageHandler<BatchCommitRequest>
{
    public async Task<ServerMessage> HandleAsync(string connectionId, BatchCommitRequest request, CancellationToken ct)
    {
        var result = await documentManager.CommitAsync(request.Batch, ct);

        return new BatchCommitResponse 
        { 
            RequestId = request.Id, 
            Result = result,
        };
    }
}
