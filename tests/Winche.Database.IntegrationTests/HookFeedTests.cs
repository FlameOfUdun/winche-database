using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Winche.Database.Abstraction;
using Winche.Database.DependencyInjection;
using Winche.Database.Documents;
using Winche.Database.Runtime.ChangeFeed;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

/// <summary>Recording hook that captures set/update/delete callback arguments.</summary>
internal sealed class RecordingHook : DocumentStoreHook
{

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
/// Shared recorder injected into <see cref="DiTestHook"/> via constructor so that the hook
/// (registered opaquely as <c>DocumentStoreHook</c>) can be observed by resolving this singleton
/// from the DI container.
/// </summary>
internal sealed class HookEventRecorder
{
    private readonly List<(string Event, string HookPath, Document? Doc)> _events = [];
    private readonly SemaphoreSlim _signal = new(0);

    public IReadOnlyList<(string Event, string HookPath, Document? Doc)> Events
    {
        get { lock (_events) return _events.ToList(); }
    }

    public void Record(string evt, string hookPath, Document? doc)
    {
        lock (_events) _events.Add((evt, hookPath, doc));
        _signal.Release();
    }

    public Task WaitForEventsAsync(int count, int timeoutMs = 10000)
        => Task.Run(async () =>
        {
            for (var i = 0; i < count; i++)
                if (!await _signal.WaitAsync(timeoutMs))
                    throw new TimeoutException($"HookEventRecorder: timed out waiting for event {i + 1}/{count}");
        });
}

/// <summary>
/// Hook for DI-wiring test. Receives a <see cref="HookEventRecorder"/> via constructor
/// injection so the test can observe events without resolving the hook by its concrete type.
/// </summary>
internal sealed class DiTestHook(HookEventRecorder recorder) : DocumentStoreHook
{

    public override Task OnDocumentSetAsync(string path, Document document, CancellationToken ct)
    {
        recorder.Record("set", path, document);
        return Task.CompletedTask;
    }

    public override Task OnDocumentUpdatedAsync(string path, Document document, CancellationToken ct)
    {
        recorder.Record("update", path, document);
        return Task.CompletedTask;
    }

    public override Task OnDocumentDeletedAsync(string path, CancellationToken ct)
    {
        recorder.Record("delete", path, null);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Hook that throws on the first N invocations, then succeeds. Used to exercise the
/// DurableConsumerRunner's at-least-once retry path.
/// </summary>
internal sealed class FlakyHook(int failCount) : DocumentStoreHook
{

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
        var hook = new RecordingHook();
        var consumer = new HookFeedConsumer([new HookRegistration("{document=**}", hook)]);

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
        var hook = new FlakyHook(failCount: 1);
        var consumer = new HookFeedConsumer([new HookRegistration("{document=**}", hook)]);

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

    /// <summary>
    /// Test 1 — hook path scoping uses Winche.Rules PathMatcher directly.
    /// A <see cref="RecordingHook"/> with a scoped path pattern (<c>scoped/{id}</c>) is wired
    /// via <see cref="HookFeedConsumer"/>; matching is delegated to
    /// <c>Winche.Rules.Matching.PathMatcher.IsMatch</c>.
    /// A non-matching document (<c>other/o1</c>) is written first; the matching document
    /// (<c>scoped/s1</c>) is written second.  After waiting for exactly one event, the test
    /// asserts that:
    /// <list type="bullet">
    ///   <item>Exactly one event was recorded (the non-matching write did NOT fire).</item>
    ///   <item>The single event is the <c>set</c> of <c>scoped/s1</c>.</item>
    /// </list>
    /// Because the pump processes records in changelog sequence order, <c>other/o1</c> was
    /// ingested before <c>scoped/s1</c> arrived — so its absence from recorded events proves
    /// the real matcher filtered it out.
    /// </summary>
    [Fact]
    public async Task HookFeed_RealPathMatcher_ScopedHookIgnoresNonMatchingPaths()
    {
        // Path pattern "scoped/{id}" matches exactly two-segment paths starting with "scoped/".
        var hook = new RecordingHook();
        var consumer = new HookFeedConsumer([new HookRegistration("scoped/{id}", hook)]);

        var pump = new ChangeFeedPump(Fx.DataSource, [consumer],
            new ChangeFeedConfig { PollInterval = TimeSpan.FromMilliseconds(200) },
            NullLogger<ChangeFeedPump>.Instance);

        var cts = new CancellationTokenSource();
        var pumpRun = Task.Run(() => pump.RunAsync(cts.Token));

        try
        {
            await Task.Delay(400);   // let pump boot and persist the initial cursor

            var applier = new WriteApplier(Fx.DataSource);

            // Non-matching write first — must NOT reach the hook.
            await applier.ApplyAsync([new SetWrite { Path = "other/o1", Fields = Map(("v", new IntegerValue(99))) }]);

            // Matching write second.
            await applier.ApplyAsync([new SetWrite { Path = "scoped/s1", Fields = Map(("v", new IntegerValue(1))) }]);

            // Wait for exactly one event.  If other/o1 had fired as well the semaphore would
            // already have been released once before scoped/s1 arrived, and WaitForEventsAsync(1)
            // would return immediately after other/o1 — but then the assertion below would fail
            // because the single recorded event would be "other/o1".  Since ordering is strictly
            // sequential (feed is monotone) other/o1 is guaranteed to have been processed first.
            await hook.WaitForEventsAsync(1);

            var events = hook.Events;
            Assert.Single(events);
            Assert.Equal("set", events[0].Event);
            Assert.Equal("scoped/s1", events[0].HookPath);
        }
        finally
        {
            await cts.CancelAsync();
            await pumpRun;
        }
    }

    /// <summary>
    /// Test 2 — <c>AddHook&lt;T&gt;</c> is wired through DI into the running feed.
    /// Builds a full DI service provider with <c>AddWincheDatabase</c>, registers
    /// <see cref="DiTestHook"/> via <c>AddHook&lt;DiTestHook&gt;</c>, starts
    /// the <see cref="ChangeFeedHostedService"/>, writes a document directly via
    /// <see cref="WriteApplier"/>, and asserts the hook received the event — proving the
    /// DI-wired change-feed hosted service delivers to hooks registered through options.
    /// </summary>
    [Fact]
    public async Task HookFeed_DiRegisteredHook_ReceivesEventsThroughHostedService()
    {
        // Shared recorder registered as a singleton so that DiTestHook (resolved opaquely
        // as DocumentStoreHook) can write events that the test can observe.
        var recorder = new HookEventRecorder();

        var services = new ServiceCollection();
        services.AddLogging();            // ILogger<T> required by ChangeFeedHostedService
        services.AddSingleton(recorder);  // injected into DiTestHook via constructor
        services.AddWincheDatabase(opts =>
        {
            opts.ConnectionString = Fx.ConnectionString;
            opts.ChangeFeed = new ChangeFeedConfig { PollInterval = TimeSpan.FromMilliseconds(200) };
            opts.UseHooks(h => h.Add<DiTestHook>("di-hook/{document=**}"));
        });

        await using var provider = services.BuildServiceProvider();

        // Start all hosted services (ChangeFeedHostedService, RetentionPruner, TransactionSweeper).
        var hostedServices = provider.GetServices<IHostedService>().ToList();
        var startCts = new CancellationTokenSource();
        foreach (var svc in hostedServices)
            await svc.StartAsync(startCts.Token);

        try
        {
            await Task.Delay(400);   // let the change-feed pump boot and persist initial cursor

            // Write directly via WriteApplier (bypasses the rules guard; the hook observes the
            // changelog post-commit, so the feed sees the write regardless of the guard).
            var applier = new WriteApplier(Fx.DataSource);
            await applier.ApplyAsync([new SetWrite
            {
                Path = "di-hook/test1",
                Fields = Map(("v", new IntegerValue(7)))
            }]);

            // Wait for the recorder to receive the event delivered by the DI-wired feed.
            await recorder.WaitForEventsAsync(1);

            var events = recorder.Events;
            Assert.Single(events);
            Assert.Equal("set", events[0].Event);
            Assert.Equal("di-hook/test1", events[0].HookPath);
        }
        finally
        {
            var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            foreach (var svc in hostedServices)
            {
                try { await svc.StopAsync(stopCts.Token); }
                catch (OperationCanceledException) { /* shutdown timeout is acceptable */ }
            }
        }
    }
}
