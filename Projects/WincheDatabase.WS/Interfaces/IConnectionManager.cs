using System.Net.WebSockets;

namespace WincheDatabase.WS.Interfaces;

public interface IConnectionManager
{
    Task AcceptAsync(WebSocket socket, IReadOnlyDictionary<string, object?> claims);
}
