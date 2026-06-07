using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Winche.Database.Models;
using Winche.Database.Runtime.ChangeFeed;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

internal sealed class CollectingConsumer : IChangeFeedConsumer
{
    public readonly List<ChangeBatch> Batches = [];
    private readonly SemaphoreSlim _signal = new(0);

    public Task OnBatchAsync(ChangeBatch batch, CancellationToken ct)
    {
        lock (Batches) Batches.Add(batch);
        _signal.Release();
        return Task.CompletedTask;
    }

    public async Task<bool> WaitForBatchAsync(int timeoutMs = 10000) => await _signal.WaitAsync(timeoutMs);

    public IEnumerable<ChangeRecord> AllRecords { get { lock (Batches) return Batches.SelectMany(b => b.Records).ToList(); } }
}

/// <summary>ILogger that counts Error+ log entries.</summary>
internal sealed class RecordingLogger : ILogger<ChangeFeedPump>
{
    private int _errorCount;
    public int ErrorCount => Volatile.Read(ref _errorCount);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= LogLevel.Error) Interlocked.Increment(ref _errorCount);
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

[Collection("postgres")]
public class ChangeFeedPumpTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private static Dictionary<string, Value> Map(params (string K, Value V)[] e) => e.ToDictionary(x => x.K, x => x.V);

    private (ChangeFeedPump Pump, CollectingConsumer Consumer, CancellationTokenSource Cts, Task Run) StartPump()
    {
        var consumer = new CollectingConsumer();
        var pump = new ChangeFeedPump(Fx.DataSource, Fx.Table, [consumer],
            new ChangeFeedConfig { PollInterval = TimeSpan.FromMilliseconds(250) },
            NullLogger<ChangeFeedPump>.Instance);
        var cts = new CancellationTokenSource();
        var run = Task.Run(() => pump.RunAsync(cts.Token));
        return (pump, consumer, cts, run);
    }

    [Fact]
    public async Task Pump_DeliversBatches_WithSharedDocs_LiveOnly()
    {
        var applier = new WriteApplier(Fx.DataSource, Fx.Table);
        await applier.ApplyAsync([new SetWrite { Path = "c/before", Fields = Map() }]);   // pre-boot: must NOT arrive

        var (_, consumer, cts, run) = StartPump();
        try
        {
            await Task.Delay(500);                                                        // pump boots, cursor at max

            await applier.ApplyAsync(
            [
                new SetWrite { Path = "c/a", Fields = Map(("x", new IntegerValue(1))) },
                new SetWrite { Path = "c/b", Fields = Map() },
            ]);

            Assert.True(await consumer.WaitForBatchAsync());
            var records = consumer.AllRecords.ToList();
            Assert.Equal(2, records.Count);
            Assert.DoesNotContain(records, r => r.Path == "c/before");
            Assert.All(records, r => Assert.Equal(ChangeType.Added, r.Type));

            var batch = consumer.Batches[0];
            Assert.Equal(new IntegerValue(1), batch.Documents["c/a"].Fields["x"]);        // shared doc fetch

            await applier.ApplyAsync([new DeleteWrite { Path = "c/a" }]);
            Assert.True(await consumer.WaitForBatchAsync());
            Assert.Contains(consumer.AllRecords, r => r is { Type: ChangeType.Removed, Path: "c/a" });
        }
        finally
        {
            cts.Cancel();
            await run;
        }
    }

    [Fact]
    public async Task Pump_SurvivesConsumerExceptions()
    {
        var bad = new ThrowingConsumer();
        var good = new CollectingConsumer();
        var pump = new ChangeFeedPump(Fx.DataSource, Fx.Table, [bad, good],
            new ChangeFeedConfig { PollInterval = TimeSpan.FromMilliseconds(250) },
            NullLogger<ChangeFeedPump>.Instance);
        var cts = new CancellationTokenSource();
        var run = Task.Run(() => pump.RunAsync(cts.Token));
        try
        {
            await Task.Delay(500);
            await new WriteApplier(Fx.DataSource, Fx.Table).ApplyAsync(
                [new SetWrite { Path = "c/x", Fields = Map() }]);
            Assert.True(await good.WaitForBatchAsync());                                  // bad consumer didn't kill delivery
        }
        finally { cts.Cancel(); await run; }
    }

    /// <summary>Regression for C1: idle poll loop must not crash (NpgsqlOperationInProgressException).</summary>
    [Fact]
    public async Task Pump_Idle_LogsNoErrors()
    {
        var logger = new RecordingLogger();
        var pump = new ChangeFeedPump(Fx.DataSource, Fx.Table, [new CollectingConsumer()],
            new ChangeFeedConfig { PollInterval = TimeSpan.FromMilliseconds(200) }, logger);
        var cts = new CancellationTokenSource();
        var run = Task.Run(() => pump.RunAsync(cts.Token));
        await Task.Delay(2000);  // ~10 idle poll cycles
        await cts.CancelAsync();
        await run;
        Assert.Equal(0, logger.ErrorCount);
    }

    private sealed class ThrowingConsumer : IChangeFeedConsumer
    {
        public Task OnBatchAsync(ChangeBatch batch, CancellationToken ct) => throw new InvalidOperationException("boom");
    }
}
