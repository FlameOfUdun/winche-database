using System.Net.WebSockets;

namespace WincheDatabase.WS.Abstraction;

public interface IConnectionManager
{
    Task AcceptAsync(WebSocket socket, IReadOnlyDictionary<string, object?> claims);
}
