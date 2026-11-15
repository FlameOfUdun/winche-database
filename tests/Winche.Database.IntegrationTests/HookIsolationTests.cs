using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Winche.Database.Abstraction;
using Winche.Database.Constants;
using Winche.Database.DependencyInjection;
using Winche.Database.Documents;
using Winche.Database.Runtime.ChangeFeed;
using Winche.Database.Runtime.Writes;
using Winche.Values;

namespace Winche.Database.IntegrationTests;

/// <summary>Fails for every path in <see cref="Failing"/>; records every call per path.</summary>
internal sealed class SelectiveFailingHook : DocumentStoreHook
{
    private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);
    private readonly HashSet<string> _delivered = new(StringComparer.Ordinal);

    public HashSet<string> Failing { get; } = new(StringComparer.Ordinal);

    public int Calls(string path) { lock (_calls) return _calls.GetValueOrDefault(path); }

    public bool Delivered(string path) { lock (_calls) return _delivered.Contains(path); }

    private Task Handle(string path)
    {
        lock (_calls)
        {
            _calls[path] = _calls.GetValueOrDefault(path) + 1;
            if (Failing.Contains(path)) throw new InvalidOperationException($"hook refuses {path}");
            _delivered.Add(path);
        }
        return Task.CompletedTask;
    }

    public override Task OnDocumentSetAsync(string path, Document document, CancellationToken ct) => Handle(path);
    public override Task OnDocumentUpdatedAsync(string path, Document document, CancellationToken ct) => Handle(path);
    public override Task OnDocumentDeletedAsync(string path, CancellationToken ct) => Handle(path);
}

/// <summary>A second hook type that always works.</summary>
internal sealed class HealthyHook : DocumentStoreHook
{
    private readonly HashSet<string> _delivered = new(StringComparer.Ordinal);

    public bool Delivered(string path) { lock (_delivered) return _delivered.Contains(path); }

    public override Task OnDocumentSetAsync(string path, Document document, CancellationToken ct)
    {
        lock (_delivered) _delivered.Add(path);
        return Task.CompletedTask;
    }
}

/// <summary>
/// 10.4.0: a cursor per hook type, so a failing hook holds back only itself; only the failing change is
/// retried; a change that keeps failing is dead-lettered and can be replayed; pruning keeps what a
/// consumer still needs.
/// </summary>
[Collection("postgres")]
public class HookIsolationTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private static Dictionary<string, Value> Map(int v) => new() { ["v"] = new IntegerValue(v) };

    private static async Task Eventually(Func<bool> condition, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(50);
        }
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(100);
        }
    }

    private sealed class Running(ChangeFeedConsumerSet set, CancellationTokenSource cts, Task run) : IAsyncDisposable
    {
        public ChangeFeedConsumerSet Set { get; } = set;

        public async ValueTask DisposeAsync()
        {
            await cts.CancelAsync();
            await run;
        }
    }

    private async Task<Running> StartAsync(ChangeFeedConfig config, params HookRegistration[] hooks)
    {
        var set = new ChangeFeedConsumerSet([], hooks);
        var pump = new ChangeFeedPump(Fx.DataSource, set.All, config, NullLogger<ChangeFeedPump>.Instance);
        var cts = new CancellationTokenSource();
        var run = Task.Run(() => pump.RunAsync(cts.Token));
        // Wait until every hook type has its cursor (a runner without one starts from now).
        await EventuallyAsync(async () =>
        {
            foreach (var name in set.HookNames)
                if (await new ChangeFeedReader(Fx.DataSource).GetPositionAsync(name) is null) return false;
            return true;
        });
        return new Running(set, cts, run);
    }

    private static ChangeFeedConfig Fast(TimeSpan? deadLetterAfter = null) => new()
    {
        PollInterval = TimeSpan.FromMilliseconds(100),
        LeaderRetryInterval = TimeSpan.FromMilliseconds(200),
        DeadLetterAfter = deadLetterAfter,
    };

    [Fact]
    public async Task A_failing_hook_type_does_not_hold_back_another()
    {
        var failing = new SelectiveFailingHook();
        failing.Failing.Add("iso/a");
        var healthy = new HealthyHook();
        await using var running = await StartAsync(Fast(),
            new HookRegistration("iso/{id}", failing), new HookRegistration("iso/{id}", healthy));

        var applier = new WriteApplier(Fx.DataSource);
        await applier.ApplyAsync([new SetWrite { Path = "iso/a", Fields = Map(1) }]);
        await applier.ApplyAsync([new SetWrite { Path = "iso/b", Fields = Map(2) }]);

        await Eventually(() => healthy.Delivered("iso/a") && healthy.Delivered("iso/b"));
        Assert.False(failing.Delivered("iso/b"));   // still stuck on iso/a, as it should be

        var status = (await new ChangeFeedConsumers(Fx.DataSource, running.Set).GetStatusAsync())
            .ToDictionary(s => s.Name);
        var stuck = status[HookFeedConsumer.DurableNameFor(typeof(SelectiveFailingHook))];
        Assert.NotNull(stuck.FailingSince);
        Assert.True(stuck.FailingAttempts >= 1);
        Assert.Contains("hook refuses iso/a", stuck.LastError);
        Assert.NotNull(stuck.OldestPendingAt);
        var fine = status[HookFeedConsumer.DurableNameFor(typeof(HealthyHook))];
        Assert.Null(fine.FailingSince);
        Assert.Null(fine.OldestPendingAt);
        Assert.Equal(TimeSpan.Zero, fine.Lag);
    }

    [Fact]
    public async Task Only_the_failing_change_is_retried_not_the_ones_before_it()
    {
        var hook = new SelectiveFailingHook();
        hook.Failing.Add("batch/2");
        await using var running = await StartAsync(Fast(), new HookRegistration("batch/{id}", hook));

        // One transaction → one batch of three changes.
        await new WriteApplier(Fx.DataSource).ApplyAsync([
            new SetWrite { Path = "batch/1", Fields = Map(1) },
            new SetWrite { Path = "batch/2", Fields = Map(2) },
            new SetWrite { Path = "batch/3", Fields = Map(3) },
        ]);

        await Eventually(() => hook.Calls("batch/2") >= 3);
        lock (hook.Failing) hook.Failing.Remove("batch/2");
        await Eventually(() => hook.Delivered("batch/3"));

        Assert.Equal(1, hook.Calls("batch/1"));   // delivered once, never redone
        Assert.Equal(1, hook.Calls("batch/3"));
    }

    [Fact]
    public async Task A_change_that_keeps_failing_is_dead_lettered_and_can_be_replayed()
    {
        var hook = new SelectiveFailingHook();
        hook.Failing.Add("dl/bad");
        await using var running = await StartAsync(Fast(deadLetterAfter: TimeSpan.FromSeconds(1)),
            new HookRegistration("dl/{id}", hook));

        var applier = new WriteApplier(Fx.DataSource);
        await applier.ApplyAsync([new SetWrite { Path = "dl/bad", Fields = Map(1) }]);
        await applier.ApplyAsync([new SetWrite { Path = "dl/good", Fields = Map(2) }]);

        await Eventually(() => hook.Delivered("dl/good"), timeoutMs: 20000);   // moved on past dl/bad

        var consumers = new ChangeFeedConsumers(Fx.DataSource, running.Set);
        var name = HookFeedConsumer.DurableNameFor(typeof(SelectiveFailingHook));
        var letter = Assert.Single(await consumers.GetDeadLettersAsync(name));
        Assert.Equal("dl/bad", letter.Change.Path);
        Assert.Equal(ChangeType.Added, letter.Change.Type);
        Assert.True(letter.Attempts >= 2);
        Assert.Contains("hook refuses dl/bad", letter.Error);
        var status = Assert.Single(await consumers.GetStatusAsync());
        Assert.Equal(1, status.DeadLetters);
        Assert.Null(status.FailingSince);

        // Still broken: replay keeps it.
        Assert.Equal(new DeadLetterReplayResult(0, 1), await consumers.ReplayDeadLettersAsync(name));
        Assert.Single(await consumers.GetDeadLettersAsync(name));

        // Fixed: replay delivers and removes it.
        lock (hook.Failing) hook.Failing.Clear();
        Assert.Equal(new DeadLetterReplayResult(1, 0), await consumers.ReplayDeadLettersAsync(name));
        Assert.True(hook.Delivered("dl/bad"));
        Assert.Empty(await consumers.GetDeadLettersAsync(name));
    }

    [Fact]
    public async Task Without_a_dead_letter_limit_a_failing_change_is_retried_for_ever()
    {
        var hook = new SelectiveFailingHook();
        hook.Failing.Add("forever/bad");
        await using var running = await StartAsync(Fast(deadLetterAfter: null), new HookRegistration("forever/{id}", hook));

        var applier = new WriteApplier(Fx.DataSource);
        await applier.ApplyAsync([new SetWrite { Path = "forever/bad", Fields = Map(1) }]);
        await applier.ApplyAsync([new SetWrite { Path = "forever/next", Fields = Map(2) }]);

        await Eventually(() => hook.Calls("forever/bad") >= 3, timeoutMs: 20000);
        Assert.False(hook.Delivered("forever/next"));
        Assert.Empty(await new ChangeFeedConsumers(Fx.DataSource, running.Set).GetDeadLettersAsync());
    }

    /// <summary>A durable consumer that doesn't say which change failed (throws a plain exception).</summary>
    private sealed class PlainConsumer(string failPath) : IChangeFeedConsumer
    {
        public List<string> Delivered { get; } = [];
        public string? DurableName => "plain";

        public Task OnBatchAsync(ChangeBatch batch, CancellationToken ct)
        {
            if (batch.Records.Any(r => r.Path == failPath)) throw new InvalidOperationException("plain failure");
            lock (Delivered) Delivered.AddRange(batch.Records.Select(r => r.Path));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_consumer_that_does_not_say_which_change_failed_is_narrowed_down_one_change_at_a_time()
    {
        var consumer = new PlainConsumer("plain/2");
        var set = new ChangeFeedConsumerSet([consumer], []);
        var pump = new ChangeFeedPump(Fx.DataSource, set.All, Fast(deadLetterAfter: TimeSpan.FromSeconds(1)), NullLogger<ChangeFeedPump>.Instance);
        using var cts = new CancellationTokenSource();
        var run = Task.Run(() => pump.RunAsync(cts.Token));
        try
        {
            await EventuallyAsync(async () => await new ChangeFeedReader(Fx.DataSource).GetPositionAsync("plain") is not null);
            await new WriteApplier(Fx.DataSource).ApplyAsync([
                new SetWrite { Path = "plain/1", Fields = Map(1) },
                new SetWrite { Path = "plain/2", Fields = Map(2) },
                new SetWrite { Path = "plain/3", Fields = Map(3) },
            ]);

            await Eventually(() => { lock (consumer.Delivered) return consumer.Delivered.Contains("plain/3"); }, timeoutMs: 20000);
            lock (consumer.Delivered) Assert.Equal(["plain/1", "plain/3"], consumer.Delivered);
            var letter = Assert.Single(await new ChangeFeedConsumers(Fx.DataSource, set).GetDeadLettersAsync("plain"));
            Assert.Equal("plain/2", letter.Change.Path);
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task Discard_drops_a_dead_letter()
    {
        var reader = new ChangeFeedReader(Fx.DataSource);
        await new WriteApplier(Fx.DataSource).ApplyAsync([new SetWrite { Path = "drop/x", Fields = Map(1) }]);
        var record = Assert.Single(await reader.ReadAfterAsync(new FeedPosition(0, 0), 10));
        await reader.SavePositionAsync("c1", new FeedPosition(0, 0));
        await reader.DeadLetterAsync("c1", record);

        var consumers = new ChangeFeedConsumers(Fx.DataSource, new ChangeFeedConsumerSet([], []));
        var letter = Assert.Single(await consumers.GetDeadLettersAsync());
        Assert.Equal(record.Position, (await reader.GetPositionAsync("c1"))!.Value);   // cursor moved past it
        Assert.True(await consumers.DiscardDeadLetterAsync(letter.Id));
        Assert.False(await consumers.DiscardDeadLetterAsync(letter.Id));
        Assert.Empty(await consumers.GetDeadLettersAsync());
    }

    // ── pruning ────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<ChangeRecord>> WriteAgedAsync(int count, TimeSpan age)
    {
        var applier = new WriteApplier(Fx.DataSource);
        for (var i = 0; i < count; i++)
            await applier.ApplyAsync([new SetWrite { Path = $"prune/{Guid.NewGuid():N}", Fields = Map(i) }]);
        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE {WincheDatabaseTables.Changes} SET commit_time = now() - $1::interval";
        cmd.Parameters.AddWithValue(age);
        await cmd.ExecuteNonQueryAsync();
        return await new ChangeFeedReader(Fx.DataSource).ReadAfterAsync(new FeedPosition(0, 0), 100);
    }

    [Fact]
    public async Task Pruning_keeps_rows_a_durable_consumer_has_not_delivered()
    {
        var rows = await WriteAgedAsync(4, TimeSpan.FromDays(8));
        var reader = new ChangeFeedReader(Fx.DataSource);
        await reader.SavePositionAsync("slow", rows[1].Position);   // delivered 2 of 4
        await reader.SavePositionAsync("fast", rows[3].Position);
        await reader.SavePositionAsync("gone", new FeedPosition(0, 0));   // not registered: ignored

        var now = DateTimeOffset.UtcNow;
        var (pruned, undelivered) = await reader.PruneAsync(now.AddDays(-7), now.AddDays(-30), ["slow", "fast"]);

        Assert.Equal((2, 0), (pruned, undelivered));
        var left = await reader.ReadAfterAsync(new FeedPosition(0, 0), 100);
        Assert.Equal([rows[2].Seq, rows[3].Seq], left.Select(r => r.Seq));
    }

    [Fact]
    public async Task Pruning_drops_everything_past_the_hard_limit_and_counts_what_was_undelivered()
    {
        var rows = await WriteAgedAsync(3, TimeSpan.FromDays(31));
        var reader = new ChangeFeedReader(Fx.DataSource);
        await reader.SavePositionAsync("stuck", rows[0].Position);

        var now = DateTimeOffset.UtcNow;
        Assert.Equal((3, 2), await reader.PruneAsync(now.AddDays(-7), now.AddDays(-30), ["stuck"]));
    }

    [Fact]
    public async Task Pruning_without_durable_cursors_is_by_age()
    {
        await WriteAgedAsync(2, TimeSpan.FromDays(8));
        var now = DateTimeOffset.UtcNow;
        Assert.Equal((2, 0), await new ChangeFeedReader(Fx.DataSource).PruneAsync(now.AddDays(-7), now.AddDays(-30), []));
    }

    [Fact]
    public async Task Pruning_keeps_recent_rows_even_when_delivered()
    {
        var rows = await WriteAgedAsync(2, TimeSpan.FromDays(1));
        var reader = new ChangeFeedReader(Fx.DataSource);
        await reader.SavePositionAsync("c", rows[^1].Position);
        var now = DateTimeOffset.UtcNow;
        Assert.Equal((0, 0), await reader.PruneAsync(now.AddDays(-7), now.AddDays(-30), ["c"]));
    }

    // ── upgrade from the shared cursor ────────────────────────────────────────────

    [Fact]
    public async Task On_upgrade_each_hook_type_starts_from_the_shared_cursor_once()
    {
        var reader = new ChangeFeedReader(Fx.DataSource);
        var legacy = new FeedPosition(0, 42);
        await reader.SavePositionAsync(HookFeedConsumer.LegacyDurableName, legacy);

        Assert.Equal(2, await reader.InheritCursorAsync("hooks", "hooks:", ["hooks:A", "hooks:B"]));
        Assert.Equal(legacy, await reader.GetPositionAsync("hooks:A"));
        Assert.Equal(legacy, await reader.GetPositionAsync("hooks:B"));

        // Later (e.g. a hook type added or renamed): no inheritance, it starts from now like any new consumer.
        Assert.Equal(0, await reader.InheritCursorAsync("hooks", "hooks:", ["hooks:A", "hooks:B", "hooks:C"]));
        Assert.Null(await reader.GetPositionAsync("hooks:C"));
    }

    [Fact]
    public async Task A_fresh_install_has_nothing_to_inherit()
    {
        var reader = new ChangeFeedReader(Fx.DataSource);
        Assert.Equal(0, await reader.InheritCursorAsync("hooks", "hooks:", ["hooks:A"]));
        Assert.Null(await reader.GetPositionAsync("hooks:A"));
    }

    [Fact]
    public void Hooks_get_one_consumer_per_type()
    {
        var a1 = new HealthyHook();
        var set = new ChangeFeedConsumerSet([], [
            new HookRegistration("a/{id}", a1),
            new HookRegistration("b/{id}", new SelectiveFailingHook()),
            new HookRegistration("c/{id}", a1),
        ]);
        Assert.Equal(
            [HookFeedConsumer.DurableNameFor(typeof(HealthyHook)), HookFeedConsumer.DurableNameFor(typeof(SelectiveFailingHook))],
            set.HookNames);
        Assert.Equal("hooks:Winche.Database.IntegrationTests.HealthyHook", set.HookNames[0]);
    }
}
