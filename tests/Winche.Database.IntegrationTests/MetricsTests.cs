using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Winche.Database.Abstraction;
using Winche.Database.AspNetCore.WebSockets.Connections;
using Winche.Database.DependencyInjection;
using Winche.Database.Documents;
using Winche.Database.IntegrationTests.Ws;
using Winche.Database.Querying;
using Winche.Database.Querying.Ast;
using Winche.Database.Runtime;
using Winche.Database.Runtime.ChangeFeed;
using Winche.Database.Runtime.Listening;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;
using Winche.Rules;
using Winche.Rules.Expressions;

namespace Winche.Database.IntegrationTests;

/// <summary>Captures Winche.Database measurements while it lives.</summary>
internal sealed class MetricCapture : IDisposable
{
    private readonly MeterListener _listener = new();
    public ConcurrentQueue<(string Name, double Value, Dictionary<string, object?> Tags)> Seen { get; } = new();

    public MetricCapture()
    {
        _listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == WincheDatabaseMetrics.MeterName) l.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Add(i, v, tags));
        _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Add(i, v, tags));
        _listener.Start();
    }

    private void Add(Instrument i, double v, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var d = new Dictionary<string, object?>();
        foreach (var t in tags) d[t.Key] = t.Value;
        Seen.Enqueue((i.Name, v, d));
    }

    public List<(double Value, Dictionary<string, object?> Tags)> Of(string name, Func<Dictionary<string, object?>, bool>? where = null) =>
        [.. Seen.Where(s => s.Name == name && (where?.Invoke(s.Tags) ?? true)).Select(s => (s.Value, s.Tags))];

    public void Dispose() => _listener.Dispose();
}

[Collection("postgres")]
public class MetricsTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private static Dictionary<string, Value> N(int n) => new() { ["n"] = new IntegerValue(n) };

    private static async Task Eventually(Func<bool> condition, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task Queries_and_writes_are_measured()
    {
        using var m = new MetricCapture();
        var db = new DocumentDatabase(Fx.DataSource, Options.Create(new WincheDatabaseOptions()));
        await db.WriteAsync([new SetWrite { Path = "metq/a", Fields = N(1) }, new SetWrite { Path = "metq/b", Fields = N(2) }]);
        await db.QueryAsync(new Query("metq"));
        await db.CountAsync(new Query("metq"));
        await db.AggregateAsync(new Query("metq"), [Aggregation.Sum("n", "s")]);
        await db.QueryAsync(new Query("metq", CollectionGroup: true));
        await Assert.ThrowsAnyAsync<Exception>(() => db.WriteAsync([]));

        bool Metq(Dictionary<string, object?> t) => (string?)t["collection_id"] == "metq";
        Assert.Equal(["aggregate", "count", "query", "query"],
            m.Of("winche.database.query.duration", Metq).Select(x => (string)x.Tags["operation"]!).Order());
        Assert.All(m.Of("winche.database.query.duration", Metq), x => Assert.Equal(false, x.Tags["indexed"]));
        Assert.Single(m.Of("winche.database.query.duration", t => Metq(t) && (bool)t["collection_group"]!));
        Assert.Contains(m.Of("winche.database.write.duration"), x => (string?)x.Tags["outcome"] == "ok");
        Assert.Contains(m.Of("winche.database.write.duration"), x => (string?)x.Tags["outcome"] == "invalid");
        Assert.Contains(m.Of("winche.database.write.documents"), x => x.Value == 2);
    }

    [Fact]
    public async Task Listeners_and_hooks_are_measured()
    {
        using var m = new MetricCapture();
        var registry = new QueryListenerRegistry(Fx.DataSource);
        var hook = new ChangeRecorder();
        var set = new ChangeFeedConsumerSet([registry], [new HookRegistration("meth/{id}", hook)]);
        var pump = new ChangeFeedPump(Fx.DataSource, set.All, new ChangeFeedConfig { PollInterval = TimeSpan.FromMilliseconds(100) },
            NullLogger<ChangeFeedPump>.Instance);
        using var cts = new CancellationTokenSource();
        var run = Task.Run(() => pump.RunAsync(cts.Token));
        var db = new DocumentDatabase(Fx.DataSource, Options.Create(new WincheDatabaseOptions()), registry);
        var consumer = set.HookNames.Single();
        try
        {
            var reader = new ChangeFeedReader(Fx.DataSource);
            await Eventually(() => reader.GetPositionAsync(consumer).GetAwaiter().GetResult() is not null);
            await using (var listener = db.Listen(new Query("meth")))
            await using (var e = listener.Snapshots().GetAsyncEnumerator())
            {
                Assert.True(await e.MoveNextAsync());
                Assert.Contains(m.Of("winche.database.listeners"), x => x.Value == 1 && (string?)x.Tags["kind"] == "query");
                await db.WriteAsync([new SetWrite { Path = "meth/a", Fields = N(1) }]);
                Assert.True(await e.MoveNextAsync());
            }
            Assert.Contains(m.Of("winche.database.listeners"), x => x.Value == -1);
            Assert.Contains(m.Of("winche.database.listener.update.duration"), x => (string?)x.Tags["method"] == "in_memory");

            await Eventually(() => hook.Changes.Count == 1);
            await Eventually(() => m.Of("winche.database.hook.changes", t => (string?)t["consumer"] == consumer).Count > 0);
            var lag = m.Of("winche.database.hook.lag", t => (string?)t["consumer"] == consumer).Last().Value;
            Assert.InRange(lag, 0, 30);
        }
        finally
        {
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task WebSocket_connections_requests_and_limits_are_measured()
    {
        using var m = new MetricCapture();
        await using var host = await WsTestHost.StartAsync(Fx.ConnectionString,
            configureDb: c => c.UseRules(r => r.Match("{path=**}", x => x.Allow(RuleOperations.Read, Expr.Const(true)))),
            configureWs: o => { o.UserIdClaimType = "uid"; o.MaxSubscriptionsPerConnection = 1; o.RequestsPerSecond = 20; o.RequestBurst = 1; },
            requireAuth: true);
        await using (var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server, "uid:metrics"))
        {
            await ws.RequestAsync(new JsonObject { ["type"] = "ping" });
            await ws.RequestAsync(new JsonObject { ["type"] = "listen", ["query"] = new JsonObject { ["collection"] = "mws" } });
            var refused = await ws.RequestAsync(new JsonObject { ["type"] = "listen", ["query"] = new JsonObject { ["collection"] = "mws2" } });
            Assert.Equal("resource-exhausted", (string?)refused["error"]!["code"]);
            await ws.RequestAsync(new JsonObject { ["type"] = "made.up" });
        }
        await Eventually(() => m.Of("winche.database.ws.connections").Sum(x => x.Value) == 0 && m.Of("winche.database.ws.connections").Count >= 2);

        Assert.Contains(m.Of("winche.database.ws.requests"), x => (string?)x.Tags["type"] == "ping");
        Assert.Contains(m.Of("winche.database.ws.requests"), x => (string?)x.Tags["type"] == "other");   // not "made.up"
        Assert.Contains(m.Of("winche.database.ws.subscriptions"), x => x.Value == 1);
        Assert.Contains(m.Of("winche.database.ws.limited"), x => (string?)x.Tags["limit"] == "subscriptions");
        Assert.NotEmpty(m.Of("winche.database.ws.throttled"));   // burst 1 at 20/s: some requests waited
        await Eventually(() => m.Of("winche.database.ws.subscriptions").Sum(x => x.Value) == 0);
    }
}
