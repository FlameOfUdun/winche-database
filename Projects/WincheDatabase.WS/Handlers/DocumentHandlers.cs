using WincheDatabase.Store.Services;
using WincheDatabase.WS.Messages;

namespace WincheDatabase.WS.Handlers;

internal sealed class DocumentGetHandler(DocumentManager documentManager) : IMessageHandler<DocumentGetRequest>
{
    public async Task<ServerMessage> HandleAsync(string connectionId, DocumentGetRequest request, CancellationToken ct)
    {
        var snapshot = await documentManager.GetAsync(request.Path, ct);

        return new DocumentGetResponse
        {
            RequestId = request.Id,
            Document = snapshot
        };
    }
}

internal sealed class DocumentSetHandler(DocumentManager documentManager) : IMessageHandler<DocumentSetRequest>
{
    public async Task<ServerMessage> HandleAsync(string connectionId, DocumentSetRequest request, CancellationToken ct)
    {
        var snapshot = await documentManager.SetAsync(request.Path, request.Data, ct);

        return new DocumentSetResponse
        {
            RequestId = request.Id,
            Document = snapshot
        };
    }
}

internal sealed class DocumentUpdateHandler(DocumentManager documentManager) : IMessageHandler<DocumentUpdateRequest>
{
    public async Task<ServerMessage> HandleAsync(string connectionId, DocumentUpdateRequest request, CancellationToken ct)
    {
        var snapshot = await documentManager.UpdateAsync(request.Path, request.Data, ct);

        return new DocumentUpdateResponse
        {
            RequestId = request.Id,
            Document = snapshot
        };
    }
}

internal sealed class DocumentDeleteHandler(DocumentManager documentManager) : IMessageHandler<DocumentDeleteRequest>
{
    public async Task<ServerMessage> HandleAsync(string connectionId, DocumentDeleteRequest request, CancellationToken ct)
    {
        await documentManager.DeleteAsync(request.Path, ct);

        return new DocumentDeleteResponse
        {
            RequestId = request.Id,
        };
    }
}
