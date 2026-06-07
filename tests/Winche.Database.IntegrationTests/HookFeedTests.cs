using Microsoft.Extensions.Logging.Abstractions;
using Winche.Database.Abstraction;
using Winche.Database.Documents;
using Winche.Database.DependencyInjection;
using Winche.Database.Runtime.ChangeFeed;
using Winche.Database.Runtime.Writes;
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

/// <summary>
/// Hook that throws on the first N invocations, then succeeds. Used to exercise the
/// DurableConsumerRunner's at-least-once retry path.
/// </summary>
internal sealed class FlakyHook(string path, int failCount) : DocumentStoreHook
{
    public override string Path => path;

    private int _invocations;
    private readonly List<(string Event, string HookPath, Document? Doc)> _events = [];
    private readonly SemaphoreSlim _signal = new(0);

    public int TotalInvocations => Volatile.Read(ref _invocations);
    public IReadOnlyList<(string Event, string HookPath, Document? Doc)> Events
    {
        get { lock (_events) return _events.ToList(); }
    }

    public Task WaitForEventsAsync(int count, int timeoutMs = 15000)
        => Task.Run(async () =>
        {
            for (var i = 0; i < count; i++)
                if (!await _signal.WaitAsync(timeoutMs)) throw new TimeoutException($"FlakyHook: timed out waiting for event {i + 1}/{count}");
        });

    private void Record(string evt, string hookPath, Document? doc)
    {
        lock (_events) _events.Add((evt, hookPath, doc));
        _signal.Release();
    }

    public override Task OnDocumentSetAsync(string path, Document document, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref _invocations);
        if (n <= failCount) throw new InvalidOperationException($"Flaky hook failure #{n}");
        Record("set", path, document);
        return Task.CompletedTask;
    }

    public override Task OnDocumentUpdatedAsync(string path, Document document, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref _invocations);
        if (n <= failCount) throw new InvalidOperationException($"Flaky hook failure #{n}");
        Record("update", path, document);
        return Task.CompletedTask;
    }

    public override Task OnDocumentDeletedAsync(string path, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref _invocations);
        if (n <= failCount) throw new InvalidOperationException($"Flaky hook failure #{n}");
        Record("delete", path, null);
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
        var consumer = new HookFeedConsumer([hook], AlwaysMatchPathMatcher.Instance);

        var pump = new ChangeFeedPump(Fx.DataSource, [consumer],
            new ChangeFeedConfig { PollInterval = TimeSpan.FromMilliseconds(200) },
            NullLogger<ChangeFeedPump>.Instance);

        var cts = new CancellationTokenSource();
        var pumpRun = Task.Run(() => pump.RunAsync(cts.Token));

        try
        {
            await Task.Delay(400);   // let pump boot

            var applier = new WriteApplier(Fx.DataSource);

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
            await pumpRun;
        }
    }

    /// <summary>
    /// AT-LEAST-ONCE regression: a hook that throws on its first invocation causes the
    /// DurableConsumerRunner to retry the same batch with backoff. After the failure the
    /// hook eventually receives the call and records exactly one success. The cursor
    /// advances only after the successful delivery.
    /// </summary>
    [Fact]
    public async Task HookFeed_FlakyHook_RetriesAndEventuallySucceeds()
    {
        // FlakyHook fails on the first invocation, succeeds from the second onwards.
        var hook = new FlakyHook("**", failCount: 1);
        var consumer = new HookFeedConsumer([hook], AlwaysMatchPathMatcher.Instance);

        // Fast backoff so the test doesn't wait 1+ second for the real default.
        var config = new ChangeFeedConfig
        {
            PollInterval = TimeSpan.FromMilliseconds(100),
        };

        var pump = new ChangeFeedPump(Fx.DataSource, [consumer],
            config,
            NullLogger<ChangeFeedPump>.Instance);

        var cts = new CancellationTokenSource();
        var pumpRun = Task.Run(() => pump.RunAsync(cts.Token));

        try
        {
            await Task.Delay(300);   // let pump boot and persist the initial cursor

            var applier = new WriteApplier(Fx.DataSource);
            await applier.ApplyAsync([new SetWrite { Path = "h/retry", Fields = Map(("v", new IntegerValue(42))) }]);

            // Wait for the hook to record exactly one success (after retrying the failed batch).
            await hook.WaitForEventsAsync(1, timeoutMs: 15000);

            // Exactly one event recorded (idempotency: not duplicated)
            Assert.Single(hook.Events);
            Assert.Equal("set", hook.Events[0].Event);
            Assert.Equal("h/retry", hook.Events[0].HookPath);

            // The runner invoked the hook at least twice (once failing, once succeeding)
            Assert.True(hook.TotalInvocations >= 2,
                $"Expected at least 2 invocations (1 fail + 1 success) but got {hook.TotalInvocations}");
        }
        finally
        {
            await cts.CancelAsync();
            await pumpRun;
        }
    }
}
