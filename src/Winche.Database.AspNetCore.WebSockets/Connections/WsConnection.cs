using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Winche.Database.AspNetCore.WebSockets.Protocol;

namespace Winche.Database.AspNetCore.WebSockets.Connections;

/// <summary>
/// Socket framing: serial receive side; bounded single-writer send side (spec §1/§3 backpressure:
/// queue overflow closes 1013). One JSON object per text frame.
/// </summary>
public sealed class WsConnection(WebSocket socket, WsOptions options) : IAsyncDisposable
{
    private readonly Channel<ServerMessage> _send = Channel.CreateBounded<ServerMessage>(
        new BoundedChannelOptions(options.SendQueueLimit) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly CancellationTokenSource _closed = new();
    private volatile bool _completed;

    public CancellationToken Closed => _closed.Token;

    /// <summary>False = queue full (pathologically slow client) → caller should expect the 1013 close.</summary>
    public bool TrySend(ServerMessage message)
    {
        if (_send.Writer.TryWrite(message)) return true;
        if (_completed) return false;                          // already draining — no spurious 1013
        _ = CloseAsync((WebSocketCloseStatus)1013, "send queue overflow");
        return false;
    }

    public async Task RunSendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var message in _send.Reader.ReadAllAsync(ct))
            {
                var json = JsonSerializer.SerializeToUtf8Bytes(message, message.GetType());
                await socket.SendAsync(json, WebSocketMessageType.Text, endOfMessage: true, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    /// <summary>
    /// Completes the send channel (so RunSendLoopAsync drains and exits), then performs
    /// the WebSocket close handshake. Call this instead of CloseAsync() when the caller
    /// already holds a reference to the sendLoop task and cannot await it independently.
    /// </summary>
    public async Task DrainAndCloseAsync(Task sendLoop, WebSocketCloseStatus status, string reason)
    {
        _completed = true;
        _closed.Cancel();
        _send.Writer.TryComplete();
        await sendLoop;                                                   // drain queued frames first
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseAsync(status, reason, CancellationToken.None);
        }
        catch (Exception) { }
    }

    /// <summary>Reads one full text frame. Returns null on close/limit violation (socket then closing).</summary>
    public async Task<JsonDocument?> ReceiveAsync(CancellationToken ct)
    {
        while (true)
        {
            var buffer = new byte[16 * 1024];
            using var frame = new MemoryStream();

            while (true)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(buffer, ct);
                }
                catch (WebSocketException) { return null; }

                if (result.MessageType == WebSocketMessageType.Close)
                    return null;
                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    await CloseAsync((WebSocketCloseStatus)4400, "binary frames are not supported");
                    return null;
                }

                frame.Write(buffer, 0, result.Count);
                if (frame.Length > options.MaxFrameBytes)
                {
                    await CloseAsync((WebSocketCloseStatus)4413, "frame too large");
                    return null;
                }
                if (result.EndOfMessage) break;
            }

            try
            {
                return JsonDocument.Parse(frame.GetBuffer().AsMemory(0, (int)frame.Length));
            }
            catch (JsonException)
            {
                TrySend(new ErrorMessage { Status = "INVALID_ARGUMENT", Message = "Malformed JSON frame." });
                // loop: read the next frame instead of recursing
            }
        }
    }

    public async Task CloseAsync(WebSocketCloseStatus status, string reason)
    {
        _completed = true;
        _closed.Cancel();
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseAsync(status, reason, CancellationToken.None);
        }
        catch (Exception) { }
    }

    public ValueTask DisposeAsync()
    {
        _completed = true;
        _closed.Cancel();
        _send.Writer.TryComplete();
        _closed.Dispose();
        return ValueTask.CompletedTask;
    }
}
