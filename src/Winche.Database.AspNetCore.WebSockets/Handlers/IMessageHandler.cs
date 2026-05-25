using Winche.Database.AspNetCore.WebSockets.Messages;

namespace Winche.Database.AspNetCore.WebSockets.Handlers;

public interface IMessageHandler<TRequest> where TRequest : ClientMessage
{
    Task<ServerMessage> HandleAsync(string connectionId, TRequest request, CancellationToken ct);
}
