using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Winche.Database.Documents;
using Winche.Database.Interfaces;
using Winche.Database.Models;
using Winche.Database.Querying.Ast;
using Winche.Database.Services;
using Winche.Database.Values;
using Winche.Sentinel.Interfaces;
using Winche.Sentinel.Models;

namespace Winche.Database.IntegrationTests;

file sealed class AllowAll2 : IAccessRuleEvaluator<Document>
{
    public Task EvaluateAsync(AccessOperation operation, string path, object? data,
        Func<CancellationToken, Task<Document?>>? getResource, CancellationToken ct = default) => Task.CompletedTask;
}

[Collection("postgres")]
public class SubscriptionFlowTests(PostgresFixture fx) : QueryTestBase(fx)
{
    [Fact]
    public async Task SubscribeThenChange_EmitsAddedEvent()
    {
        var manager = new DocumentManager(fx.DataSource, Options.Create(new StoreOptions { TableName = fx.Table }),
            new AllowAll2(), new HookInvocationDispatcher([], new NoOpMatcher()), NullLogger<DocumentManager>.Instance);
        var registry = new SubscriptionRegistry();
        var channel = new EventChannel();
        var subs = new SubscriptionManager(registry, manager);
        var processor = new ChangeProcessor(registry, manager, channel, NullLogger<ChangeProcessor>.Instance);

        await manager.SetAsync("users/u1", new Dictionary<string, Value> { ["age"] = new IntegerValue(10) });

        var query = new QueryAst("users",
            Where: new FieldFilterAst(F("age"), FilterOperator.Gte, new IntegerValue(18)));
        var sub = await subs.SubscribeAsync(query);
        Assert.Empty(sub.Result.Documents);                       // u1 is under 18

        // a matching doc arrives → simulate the notify
        var doc = await manager.SetAsync("users/u2", new Dictionary<string, Value> { ["age"] = new IntegerValue(30) });
        await processor.ProcessAsync(new DocumentChange
        {
            Type = DocumentChangeType.Added, Id = "u2", Collection = "users", Path = "users/u2",
            Version = doc.Version,
        });

        var events = await ReadOneBatchAsync(channel);
        var ev = Assert.Single(events!);
        Assert.Equal(sub.Id, ev.SubscriptionId);
        Assert.Equal(QueryChangeType.Added, ev.Change.Type);
        Assert.Equal("u2", ev.Change.DocumentId);
        Assert.Equal(new IntegerValue(30), ev.Change.Document!.Fields["age"]);

        // a NON-matching change must produce no events (ChangeMatcher short-circuits)
        await manager.SetAsync("users/u3", new Dictionary<string, Value> { ["age"] = new IntegerValue(5) });
        await processor.ProcessAsync(new DocumentChange
        {
            Type = DocumentChangeType.Added, Id = "u3", Collection = "users", Path = "users/u3", Version = 1,
        });
        // nothing new in the channel
        var second = await ReadOneBatchAsync(channel, timeoutMs: 500);
        Assert.Null(second);
    }

    private static async Task<List<SubscriptionEvent>?> ReadOneBatchAsync(EventChannel channel, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await foreach (var batch in channel.ReadAsync(cts.Token))
                return batch;
        }
        catch (OperationCanceledException) { }
        return null;
    }
}
