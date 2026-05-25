using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Winche.Database.AspNetCore.WebSockets.Operands;

public sealed class WebSocketConnection(WebSocket socket) : IAsyncDisposable
{
    private const int ReceiveBufferSize = 4096;
    private const int SendBufferSize = 8192;

    private readonly WebSocket _socket = socket;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _disposed = 0;
    
    public DateTime ConnectedAt { get; } = DateTime.UtcNow;
    public bool IsOpen => _socket.State == WebSocketState.Open;
    public CancellationToken CancellationToken => _cts.Token;
    public event Func<Task>? OnClosed;

    public async Task SendAsync<T>(T message, CancellationToken ct = default) where T : class
    {
        if (!IsOpen) return;

        await _sendLock.WaitAsync(ct);
        try
        {
            var json = JsonSerializer.Serialize(message, message.GetType());
            var byteCount = Encoding.UTF8.GetByteCount(json);
            var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                var written = Encoding.UTF8.GetBytes(json, buffer);
                await _socket.SendAsync(buffer.AsMemory(0, written), WebSocketMessageType.Text, true, ct);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Sends multiple messages in a single batch operation.
    /// Each message is sent as a separate WebSocket frame but within a single lock acquisition.
    /// </summary>
    public async Task SendBatchAsync<T>(IReadOnlyList<T> messages, CancellationToken ct = default) where T : class
    {
        if (!IsOpen || messages.Count == 0) return;

        await _sendLock.WaitAsync(ct);
        var buffer = ArrayPool<byte>.Shared.Rent(SendBufferSize);
        try
        {
            foreach (var message in messages)
            {
                if (!IsOpen) break;
                var json = JsonSerializer.Serialize(message, message.GetType());
                var byteCount = Encoding.UTF8.GetByteCount(json);

                // Resize buffer if needed
                if (byteCount > buffer.Length)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = ArrayPool<byte>.Shared.Rent(byteCount);
                }

                var written = Encoding.UTF8.GetBytes(json, buffer);
                await _socket.SendAsync(buffer.AsMemory(0, written), WebSocketMessageType.Text, true, ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            _sendLock.Release();
        }
    }

    public async Task<JsonDocument?> ReceiveAsync(CancellationToken ct = default)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
        var messageBuilder = new StringBuilder();

        try
        {
            while (true)
            {
                var result = await _socket.ReceiveAsync(buffer.AsMemory(), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await FireClosedAsync();
                    return null;
                }

                messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                if (result.EndOfMessage)
                    break;
            }

            return JsonDocument.Parse(messageBuilder.ToString());
        }
        catch (OperationCanceledException)
        {
            await FireClosedAsync();
            return null;
        }
        catch (WebSocketException)
        {
            await FireClosedAsync();
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task CloseAsync(
        string reason = "Connection closed",
        WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure,
        CancellationToken ct = default)
    {
        _cts.Cancel();

        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                await _socket.CloseAsync(status, reason, CancellationToken.None);
            }
            catch { /* client already disconnected */ }
        }

        await FireClosedAsync();
    }

    private async Task FireClosedAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        if (OnClosed is not null)
            await OnClosed.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await FireClosedAsync();
        _cts.Dispose();
        _sendLock.Dispose();
        _socket.Dispose();
        GC.SuppressFinalize(this);
    }
}
