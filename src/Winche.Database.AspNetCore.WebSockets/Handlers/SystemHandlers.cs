using Winche.Database.AspNetCore.WebSockets.Messages;

namespace Winche.Database.AspNetCore.WebSockets.Handlers;

public sealed class SystemPingHandler : IMessageHandler<SystemPingRequest>
{
    public async Task<ServerMessage> HandleAsync(string connectionId, SystemPingRequest request, CancellationToken ct)
    {
        return new SystemPongResponse
        {
            RequestId = request.Id
        };
    }
}
