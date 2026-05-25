using Winche.Database.AspNetCore.WebSockets.Messages;
using Winche.Database.Interfaces;

namespace Winche.Database.AspNetCore.WebSockets.Handlers;

internal sealed class AggregateExecuteHandler(
    IDocumentManager documentManager
) : IMessageHandler<AggregateExecuteRequest>
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
