using WincheDb.DocumentStore.Abstraction;
using WincheDb.DocumentStore.Models;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using WincheDb.Core.Ast;
using WincheDb.Realtime.Services;
using System.Collections.Frozen;
using System.Collections.ObjectModel;

namespace WebApi.Controllers;

[ApiController]
[Route("documents")]
public class DocumentsController(
    ConnectionManager connectionManager
) : ControllerBase
{
    [HttpGet("ws")]
    public async Task ConnectToWebSocket()
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsync("WebSocket connection required");
            return;
        }

        var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        await connectionManager.AcceptAsync(socket, new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>()
        {
            ["uid"] = "123"
        }));
    }
}