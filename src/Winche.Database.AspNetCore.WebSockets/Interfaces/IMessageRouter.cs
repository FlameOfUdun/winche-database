using System.Text.Json;
using Winche.Database.AspNetCore.WebSockets.Operands;

namespace Winche.Database.AspNetCore.WebSockets.Interfaces;
public interface IMessageRouter
{
    Task HandleMessageAsync(string connectionId, WebSocketConnection connection, JsonDocument document, CancellationToken ct);
}
