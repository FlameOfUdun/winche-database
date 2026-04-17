using WincheDatabase.Store.Models;
using WincheDatabase.Store.Services;
using WincheDatabase.WS.Messages;

namespace WincheDatabase.WS.Handlers;

internal sealed class SyncPushHandler(DocumentManager documentManager) : IMessageHandler<SyncPushRequest>
{
    public async Task<ServerMessage> HandleAsync(string connectionId, SyncPushRequest request, CancellationToken ct)
    {
        if (request.Batch.Mutations.Count == 0)
        {
            return new SyncPushResponse
            {
                RequestId = request.Id,
                Result = new SyncResult
                {
                    Path = request.Batch.Path,
                    Document = await documentManager.GetAsync(request.Batch.Path, ct),
                    AppliedCount = 0,
                    HasConflict = false,
                },
            };
        }

        var result = await documentManager.SyncAsync(request.Batch, ct);

        return new SyncPushResponse
        {
            RequestId = request.Id,
            Result = result,
        };
    }
}
