using System.Text.Json;
using WincheDatabase.WS.Operands;

namespace WincheDatabase.WS.Interfaces;
public interface IMessageRouter
{
    Task HandleMessageAsync(string connectionId, WebSocketConnection connection, JsonDocument document, CancellationToken ct);
}
