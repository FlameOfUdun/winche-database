using WincheDatabase.WS.Messages;

namespace WincheDatabase.WS.Handlers;

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
