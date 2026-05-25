using System.Net.WebSockets;

namespace Winche.Database.AspNetCore.WebSockets.Interfaces;

public interface IConnectionManager
{
    Task AcceptAsync(WebSocket socket, IReadOnlyDictionary<string, object?> claims);
}
