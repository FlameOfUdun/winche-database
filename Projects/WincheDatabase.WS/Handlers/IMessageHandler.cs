using WincheDatabase.WS.Messages;

namespace WincheDatabase.WS.Handlers;

public interface IMessageHandler<TRequest> where TRequest : ClientMessage
{
    Task<ServerMessage> HandleAsync(string connectionId, TRequest request, CancellationToken ct);
}
