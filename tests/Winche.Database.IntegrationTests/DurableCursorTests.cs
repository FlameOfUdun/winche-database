using Microsoft.Extensions.Logging.Abstractions;
using Winche.Database.DependencyInjection;
using Winche.Database.Runtime.ChangeFeed;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

internal sealed class DurableCollector(string name) : IChangeFeedConsumer
{
    public string? DurableName => name;
    public readonly List<ChangeRecord> Records = [];
    public int FailNextDeliveries;                       // throws this many times before succeeding
    private readonly SemaphoreSlim _signal = new(0);

    public Task OnBatchAsync(ChangeBatch batch, CancellationToken ct)
    {
        if (FailNextDeliveries > 0)
        {
            FailNextDeliveries--;
            throw new InvalidOperationException("induced failure");
        }
        lock (Records) Records.AddRange(batch.Records);
        _signal.Release();
        return Task.CompletedTask;
    }

    public Task<bool> WaitAsync(int timeoutMs = 15000) => _signal.WaitAsync(timeoutMs);
    public List<string> Paths { get { lock (Records) return [.. Records.Select(r => r.Path)]; } }
}

[Collection("postgres")]
public class DurableCursorTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private static Dictionary<string, Value> Map() => new();
    private WriteApplier Applier() => new(Fx.DataSource);

    private (CancellationTokenSource Cts, Task Run) StartPump(params IChangeFeedConsumer[] consumers)
    {
        var pump = new ChangeFeedPump(Fx.DataSource, consumers,
            new ChangeFeedConfig { PollInterval = TimeSpan.FromMilliseconds(200) },
            NullLogger<ChangeFeedPump>.Instance);
        var cts = new CancellationTokenSource();
        return (cts, Task.Run(() => pump.RunAsync(cts.Token)));
    }

    [Fact]
    public async Task CatchUp_DeliversWritesMadeWhilePumpWasDown()
    {
        var name = $"t1_{Guid.NewGuid():N}";
        var collector = new DurableCollector(name);

        // first pump run pins the cursor
        var (cts1, run1) = StartPump(collector);
        await Task.Delay(600);
        await Applier().ApplyAsync([new SetWrite { Path = "dc/live", Fields = Map() }]);
        Assert.True(await collector.WaitAsync());
        cts1.Cancel(); await run1;

        // write WHILE DOWN
        await Applier().ApplyAsync([new SetWrite { Path = "dc/whileDown", Fields = Map() }]);

        // restart with a FRESH collector instance, same durable name → catches up
        var collector2 = new DurableCollector(name);
        var (cts2, run2) = StartPump(collector2);
        try
        {
            Assert.True(await collector2.WaitAsync());
            Assert.Contains("dc/whileDown", collector2.Paths);
            Assert.DoesNotContain("dc/live", collector2.Paths);          // not redelivered (cursor persisted)
        }
        finally { cts2.Cancel(); await run2; }
    }

    [Fact]
    public async Task FirstBoot_NoHistoricalReplay()
    {
        await Applier().ApplyAsync([new SetWrite { Path = "dc/historic", Fields = Map() }]);

        var collector = new DurableCollector($"t2_{Guid.NewGuid():N}");
        var (cts, run) = StartPump(collector);
        try
        {
            await Task.Delay(600);
            await Applier().ApplyAsync([new SetWrite { Path = "dc/fresh", Fields = Map() }]);
            Assert.True(await collector.WaitAsync());
            Assert.DoesNotContain("dc/historic", collector.Paths);
            Assert.Contains("dc/fresh", collector.Paths);
        }
        finally { cts.Cancel(); await run; }
    }

    [Fact]
    public async Task FailingDelivery_RetriesSameBatch_ThenAdvances()
    {
        var collector = new DurableCollector($"t3_{Guid.NewGuid():N}") { FailNextDeliveries = 2 };
        var (cts, run) = StartPump(collector);
        try
        {
            await Task.Delay(600);
            await Applier().ApplyAsync([new SetWrite { Path = "dc/retry", Fields = Map() }]);
            Assert.True(await collector.WaitAsync(30000));               // survives 2 induced failures (1s+2s=3s backoff)
            Assert.Contains("dc/retry", collector.Paths);
            Assert.Single(collector.Paths, p => p == "dc/retry");        // delivered exactly once on success
        }
        finally { cts.Cancel(); await run; }
    }

    [Fact]
    public async Task EphemeralConsumers_Unaffected_ByDurableSiblings()
    {
        var durable = new DurableCollector($"t4_{Guid.NewGuid():N}");
        var ephemeral = new CollectingConsumer();                        // existing helper (ChangeFeedPumpTests)
        var (cts, run) = StartPump(durable, ephemeral);
        try
        {
            await Task.Delay(600);
            await Applier().ApplyAsync([new SetWrite { Path = "dc/both", Fields = Map() }]);
            Assert.True(await durable.WaitAsync());
            Assert.True(await ephemeral.WaitForBatchAsync());
        }
        finally { cts.Cancel(); await run; }
    }
}
