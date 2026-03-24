using WincheDb.Realtime.Messages;

namespace WincheDb.Realtime.Handlers;

public interface IMessageHandler<TRequest> where TRequest : ClientMessage
{
    Task<ServerMessage> HandleAsync(string connectionId, TRequest request, CancellationToken ct);
}
