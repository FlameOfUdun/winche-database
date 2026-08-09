using System.Net.WebSockets;
using System.Text;
using Winche.Database.AspNetCore.WebSockets.Connections;
using Winche.Database.AspNetCore.WebSockets.Protocol;

namespace Winche.Database.Tests.Ws;

/// <summary>Minimal <see cref="WebSocket"/> fake so drain/close ordering can be tested without a real socket.</summary>
internal sealed class RecordingWebSocket : WebSocket
{
    private WebSocketState _state = WebSocketState.Open;
    public List<string> SentFrames { get; } = [];

    /// <summary>Runs before each frame is recorded — lets a test simulate a slow/wedged send.</summary>
    public Func<CancellationToken, Task>? BeforeSend { get; set; }

    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => _state;
    public override string? SubProtocol => null;

    public override void Abort() => _state = WebSocketState.Aborted;

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        _state = WebSocketState.Closed;
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        _state = WebSocketState.CloseSent;
        return Task.CompletedTask;
    }

    public override void Dispose() { }

    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
        throw new NotSupportedException("not exercised by these tests");

    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        if (BeforeSend is { } hook) await hook(cancellationToken);
        SentFrames.Add(Encoding.UTF8.GetString(buffer.AsSpan(buffer.Offset, buffer.Count)));
    }
}

public class WsConnectionDrainTests
{
    // ── Regression: a frame queued before the drain starts must still reach the socket ──
    // RunSendLoopAsync is deferred onto the thread pool via Task.Run so it cannot have
    // started reading the channel yet — TrySend and DrainAndCloseAsync's synchronous
    // prologue both run on this thread with nothing to yield to in between. Whether the
    // frame survives depends entirely on whether the queue is drained before Closed is
    // cancelled, not on any scheduling luck.
    [Fact]
    public async Task DrainAndCloseAsync_DeliversFrameQueuedBeforeItRuns()
    {
        var socket = new RecordingWebSocket();
        var conn = new WsConnection(socket, new WsOptions());

        var sendLoop = Task.Run(() => conn.RunSendLoopAsync(conn.Closed));
        conn.TrySend(new ErrorMessage { Status = "TEST", Message = "queued" });

        await conn.DrainAndCloseAsync(sendLoop, WebSocketCloseStatus.NormalClosure, "bye");

        var sent = Assert.Single(socket.SentFrames);
        Assert.Contains("queued", sent);
        Assert.Equal(WebSocketState.Closed, socket.State);
    }

    // ── A wedged client must not hang teardown: the drain is bounded by WsOptions.DrainTimeout ──
    [Fact]
    public async Task DrainAndCloseAsync_ClosesPromptly_WhenSendIsWedged()
    {
        var socket = new RecordingWebSocket
        {
            BeforeSend = ct => Task.Delay(Timeout.Infinite, ct), // never completes on its own
        };
        var conn = new WsConnection(socket, new WsOptions { DrainTimeout = TimeSpan.FromMilliseconds(200) });

        var sendLoop = conn.RunSendLoopAsync(conn.Closed);
        conn.TrySend(new ErrorMessage { Status = "TEST", Message = "stuck" });

        var task = conn.DrainAndCloseAsync(sendLoop, WebSocketCloseStatus.NormalClosure, "bye");
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(task, completed);
        await task; // rethrows if DrainAndCloseAsync itself faulted
        Assert.Equal(WebSocketState.Closed, socket.State);
        Assert.Empty(socket.SentFrames); // the wedged send never got the chance to complete

        // The cancellation triggered by the drain timeout must unstick the wedged send loop —
        // otherwise its exception would surface as unobserved rather than being caught here.
        await sendLoop;
    }
}
