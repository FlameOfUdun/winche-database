using Microsoft.Extensions.Logging.Abstractions;
using Winche.Database.Abstraction;
using Winche.Database.Documents;
using Winche.Database.Models;
using Winche.Database.Runtime.ChangeFeed;
using Winche.Database.Runtime.Writes;
using Winche.Database.Services;
using Winche.Database.Values;
using Winche.Sentinel.Interfaces;
using Winche.Sentinel.Models;

namespace Winche.Database.IntegrationTests;

/// <summary>
/// Always-match stub for IPathPatternMatcher&lt;Document&gt;: every path matches every pattern,
/// used when we want the hook to fire for all paths.
/// </summary>
internal sealed class AlwaysMatchPathMatcher : IPathPatternMatcher<Document>
{
    public static readonly AlwaysMatchPathMatcher Instance = new();
    public PathMatchResult Match(string pattern, string path) =>
        new PathMatchResult(true, new Dictionary<string, string>());
}

/// <summary>Recording hook that captures set/update/delete callback arguments.</summary>
internal sealed class RecordingHook(string path) : DocumentStoreHook
{
    public override string Path => path;

    private readonly List<(string Event, string HookPath, Document? Doc)> _events = [];
    private readonly SemaphoreSlim _signal = new(0);

    public IReadOnlyList<(string Event, string HookPath, Document? Doc)> Events
    {
        get { lock (_events) return _events.ToList(); }
    }

    public Task WaitForEventsAsync(int count, int timeoutMs = 10000)
        => Task.Run(async () =>
        {
            for (var i = 0; i < count; i++)
                if (!await _signal.WaitAsync(timeoutMs)) throw new TimeoutException($"Hook: timed out waiting for event {i + 1}/{count}");
        });

    public override Task OnDocumentSetAsync(string path, Document document, CancellationToken ct)
    {
        lock (_events) _events.Add(("set", path, document));
        _signal.Release();
        return Task.CompletedTask;
    }

    public override Task OnDocumentUpdatedAsync(string path, Document document, CancellationToken ct)
    {
        lock (_events) _events.Add(("update", path, document));
        _signal.Release();
        return Task.CompletedTask;
    }

    public override Task OnDocumentDeletedAsync(string path, CancellationToken ct)
    {
        lock (_events) _events.Add(("delete", path, null));
        _signal.Release();
        return Task.CompletedTask;
    }
}

[Collection("postgres")]
public class HookFeedTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private static Dictionary<string, Value> Map(params (string K, Value V)[] e) => e.ToDictionary(x => x.K, x => x.V);

    [Fact]
    public async Task HookFeed_SetUpdateDelete_CallbacksArrive()
    {
        var hook = new RecordingHook("**");
        var dispatcher = new HookInvocationDispatcher([hook], AlwaysMatchPathMatcher.Instance);
        var consumer = new HookFeedConsumer(dispatcher);

        var pump = new ChangeFeedPump(Fx.DataSource, Fx.Table, [consumer],
            new ChangeFeedConfig { PollInterval = TimeSpan.FromMilliseconds(200) },
            NullLogger<ChangeFeedPump>.Instance);

        var cts = new CancellationTokenSource();
        var pumpRun = Task.Run(() => pump.RunAsync(cts.Token));

        // Drain the dispatcher channels concurrently (simulating HookInvocationProcessor inline)
        var drainCts = new CancellationTokenSource();
        var drainTasks = dispatcher.Readers
            .Select(r => DrainChannelAsync(r.Reader, drainCts.Token))
            .ToList();

        try
        {
            await Task.Delay(400);   // let pump boot

            var applier = new WriteApplier(Fx.DataSource, Fx.Table);

            // 1. Set (added)
            await applier.ApplyAsync([new SetWrite { Path = "h/doc", Fields = Map(("v", new IntegerValue(1))) }]);
            await hook.WaitForEventsAsync(1);
            Assert.Contains(hook.Events, e => e.Event == "set" && e.HookPath == "h/doc");

            // 2. Update (modified)
            await applier.ApplyAsync([new SetWrite { Path = "h/doc", Fields = Map(("v", new IntegerValue(2))) }]);
            await hook.WaitForEventsAsync(1);                                // wait for one more event
            Assert.Contains(hook.Events, e => e.Event == "update" && e.HookPath == "h/doc");

            // 3. Delete (removed)
            await applier.ApplyAsync([new DeleteWrite { Path = "h/doc" }]);
            await hook.WaitForEventsAsync(1);                                // wait for one more event
            Assert.Contains(hook.Events, e => e.Event == "delete" && e.HookPath == "h/doc");
        }
        finally
        {
            await cts.CancelAsync();
            dispatcher.Complete();
            await pumpRun;
            drainCts.Cancel();
            await Task.WhenAll(drainTasks);
        }
    }

    private static async Task DrainChannelAsync(
        System.Threading.Channels.ChannelReader<HookInvocation> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var invocation in reader.ReadAllAsync(CancellationToken.None).WithCancellation(ct))
            {
                try { await invocation.Invoke(ct); }
                catch { /* ignore errors in test */ }
            }
        }
        catch (OperationCanceledException) { /* expected on teardown */ }
    }
}
