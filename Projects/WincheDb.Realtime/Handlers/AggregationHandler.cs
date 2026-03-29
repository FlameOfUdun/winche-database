using WincheDb.DocumentStore.Services;
using WincheDb.Realtime.Messages;
using WincheDb.Realtime.Stores;

namespace WincheDb.Realtime.Handlers;

internal sealed class AggregateExecuteHandler(DocumentManager documentManager) : IMessageHandler<AggregateExecuteRequest>
{
    public async Task<ServerMessage> HandleAsync(string connectionId, AggregateExecuteRequest request, CancellationToken ct)
    {
        var result = await documentManager.AggregateAsync(request.Pipeline, ct);

        return new AggregateExecuteResponse
        {
            RequestId = request.Id,
            Result = result,
        };
    }
}
