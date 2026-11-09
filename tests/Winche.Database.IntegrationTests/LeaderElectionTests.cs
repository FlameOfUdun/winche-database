using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Winche.Database.DependencyInjection;
using Winche.Database.Runtime.ChangeFeed;
using Winche.Database.Runtime.Coordination;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

/// <summary>
/// Several nodes share one database: durable consumers (hooks) must run on exactly one of them, and
/// another node must take over — from the persisted cursor — when it stops.
/// </summary>
[Collection("postgres")]
public class LeaderElectionTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private static readonly ChangeFeedConfig Config = new()
    {
        PollInterval = TimeSpan.FromMilliseconds(200),
        LeaderRetryInterval = TimeSpan.FromMilliseconds(200),
        LeaseHeartbeat = TimeSpan.FromMilliseconds(200),
    };

    private (CancellationTokenSource Cts, Task Run) StartNode(IChangeFeedConsumer consumer)
    {
        var pump = new ChangeFeedPump(Fx.DataSource, [consumer], Config, NullLogger<ChangeFeedPump>.Instance);
        var cts = new CancellationTokenSource();
        return (cts, Task.Run(() => pump.RunAsync(cts.Token)));
    }

    private Task WriteAsync(string path) =>
        new WriteApplier(Fx.DataSource).ApplyAsync([new SetWrite { Path = path, Fields = new Dictionary<string, Value>() }]);

    private static async Task Eventually(Func<bool> condition, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task A_lock_is_held_by_one_session_at_a_time()
    {
        var name = $"lock_{Guid.NewGuid():N}";
        var first = await AdvisoryLease.TryAcquireAsync(Fx.DataSource, name, TimeSpan.FromSeconds(30));
        Assert.NotNull(first);
        Assert.Null(await AdvisoryLease.TryAcquireAsync(Fx.DataSource, name, TimeSpan.FromSeconds(30)));

        await first!.DisposeAsync();

        await using var second = await AdvisoryLease.TryAcquireAsync(Fx.DataSource, name, TimeSpan.FromSeconds(30));
        Assert.NotNull(second);
    }

    [Fact]
    public async Task A_released_connection_does_not_keep_the_lock_in_the_pool()
    {
        var name = $"lock_{Guid.NewGuid():N}";
        for (var i = 0; i < 5; i++)
        {
            await using var lease = await AdvisoryLease.TryAcquireAsync(Fx.DataSource, name, TimeSpan.FromSeconds(30));
            Assert.NotNull(lease);
        }
    }

    [Fact]
    public async Task Losing_the_connection_signals_Lost_and_frees_the_lock()
    {
        var name = $"lock_{Guid.NewGuid():N}";
        await using var lease = (await AdvisoryLease.TryAcquireAsync(Fx.DataSource, name, TimeSpan.FromMilliseconds(100)))!;

        // Kill the backend holding the lock, as a crashed node or a dropped connection would.
        await using (var conn = await Fx.DataSource.OpenConnectionAsync())
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT pg_terminate_backend(pid) FROM pg_locks WHERE locktype = 'advisory' AND objid = (@key & 4294967295)::oid AND classid = ((@key >> 32) & 4294967295)::oid";
            cmd.Parameters.AddWithValue("key", AdvisoryLease.KeyFor(name));
            await cmd.ExecuteNonQueryAsync();
        }

        await Eventually(() => lease.Lost.IsCancellationRequested);
        await using var other = await AdvisoryLease.TryAcquireAsync(Fx.DataSource, name, TimeSpan.FromSeconds(30));
        Assert.NotNull(other);
    }

    [Fact]
    public async Task Only_one_node_delivers_and_another_takes_over_from_the_cursor()
    {
        var name = $"leader_{Guid.NewGuid():N}";
        var a = new DurableCollector(name);
        var b = new DurableCollector(name);
        var (ctsA, runA) = StartNode(a);
        var (ctsB, runB) = StartNode(b);
        await Task.Delay(1000);

        for (var i = 0; i < 5; i++) await WriteAsync($"le_{name}/d{i}");
        await Eventually(() => a.Paths.Count + b.Paths.Count >= 5);
        await Task.Delay(800); // would a second node deliver them too?

        Assert.Equal(5, a.Paths.Count + b.Paths.Count);
        var (leader, leaderCts, leaderRun, standby) = a.Paths.Count > 0 ? (a, ctsA, runA, b) : (b, ctsB, runB, a);
        Assert.Empty(standby.Paths);

        // The leader stops (deploy, crash); the standby takes over and continues without replay.
        leaderCts.Cancel();
        await leaderRun;
        for (var i = 5; i < 8; i++) await WriteAsync($"le_{name}/d{i}");
        await Eventually(() => standby.Paths.Count >= 3);
        await Task.Delay(500);

        Assert.Equal([$"le_{name}/d5", $"le_{name}/d6", $"le_{name}/d7"], standby.Paths);

        ctsA.Cancel(); ctsB.Cancel();
        await Task.WhenAll(runA, runB);
    }
}
