using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.TestHost;

namespace Winche.Database.IntegrationTests.Ws;

/// <summary>Frame-level test client: send JSON, await frames by predicate with timeouts.</summary>
public sealed class WsTestClient : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly List<JsonObject> _inbox = [];
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _receiveLoop;
    private int _nextId;

    public WebSocketCloseStatus? CloseStatus { get; private set; }

    private WsTestClient(WebSocket socket)
    {
        _socket = socket;
        _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    public static async Task<WsTestClient> ConnectAsync(TestServer server, string path = "/documents/ws")
    {
        var client = server.CreateWebSocketClient();
        var socket = await client.ConnectAsync(new Uri(server.BaseAddress, path), CancellationToken.None);
        return new WsTestClient(socket);
    }

    /// <summary>
    /// Connect with optional <c>access_token</c> query parameter and await the server-initiated
    /// <c>welcome</c> frame. No hello frame is sent — the server sends welcome immediately after
    /// the upgrade.
    /// </summary>
    public static async Task<WsTestClient> ConnectAndWelcomeAsync(TestServer server, string? token = null)
    {
        var path = token is not null
            ? $"/documents/ws?access_token={Uri.EscapeDataString(token)}"
            : "/documents/ws";
        var ws = await ConnectAsync(server, path);
        await ws.WaitForAsync(f => (string?)f["type"] == "welcome");
        return ws;
    }

    public async Task SendAsync(JsonObject frame)
    {
        var bytes = Encoding.UTF8.GetBytes(frame.ToJsonString());
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public async Task SendRawAsync(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public async Task SendBinaryAsync(byte[] data)
    {
        await _socket.SendAsync(data, WebSocketMessageType.Binary, true, CancellationToken.None);
    }

    /// <summary>Sends a request with an auto id; returns the terminal response/error frame.</summary>
    public async Task<JsonObject> RequestAsync(JsonObject frame, int timeoutMs = 10000)
    {
        var id = $"r{Interlocked.Increment(ref _nextId)}";
        frame["id"] = id;
        await SendAsync(frame);
        return await WaitForAsync(f => (string?)f["id"] == id, timeoutMs);
    }

    public async Task<JsonObject> WaitForAsync(Func<JsonObject, bool> predicate, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            lock (_inbox)
            {
                var hit = _inbox.FirstOrDefault(predicate);
                if (hit is not null) { _inbox.Remove(hit); return hit; }
            }
            var remaining = deadline - DateTime.UtcNow;
            Assert.True(remaining > TimeSpan.Zero, "timed out waiting for frame");
            await _signal.WaitAsync(remaining);
        }
    }

    /// <summary>Asserts NO frame matching the predicate arrives within the window.</summary>
    public async Task AssertSilenceAsync(Func<JsonObject, bool> predicate, int windowMs = 1500)
    {
        await Task.Delay(windowMs);
        lock (_inbox) Assert.DoesNotContain(_inbox, x => predicate(x));
    }

    public Task WaitForCloseAsync(int timeoutMs = 10000) =>
        _receiveLoop.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[1024 * 1024];
        try
        {
            while (true)
            {
                using var frame = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        CloseStatus = result.CloseStatus;
                        return;
                    }
                    frame.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var obj = JsonNode.Parse(frame.GetBuffer().AsSpan(0, (int)frame.Length))!.AsObject();
                lock (_inbox) _inbox.Add(obj);
                _signal.Release();
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { CloseStatus ??= _socket.CloseStatus; }
        catch (IOException) { CloseStatus ??= _socket.CloseStatus; }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _socket.Abort(); } catch { }
        try { await _receiveLoop; } catch { }
        _socket.Dispose();
    }
}
