using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Winche.Database.Documents;
using Winche.Database.Models;
using Winche.Database.Querying.Ast;
using Winche.Database.Services;
using Winche.Database.Values;
using Winche.Sentinel.Interfaces;
using Winche.Sentinel.Models;

namespace Winche.Database.IntegrationTests;

file sealed class AllowAll : IAccessRuleEvaluator<Document>
{
    public Task EvaluateAsync(AccessOperation operation, string path, object? data,
        Func<CancellationToken, Task<Document?>>? getResource, CancellationToken ct = default) => Task.CompletedTask;
}

[Collection("postgres")]
public class DocumentManagerTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private DocumentManager Manager() => new(
        fx.DataSource,
        Options.Create(new StoreOptions { TableName = fx.Table }),
        new AllowAll(),
        new HookInvocationDispatcher([], new NoOpMatcher()),
        NullLogger<DocumentManager>.Instance);

    [Fact]
    public async Task CrudQueryAggregate_EndToEndThroughManager()
    {
        var m = Manager();
        await m.SetAsync("users/u1", new Dictionary<string, Value> { ["age"] = new IntegerValue(30) });
        await m.SetAsync("users/u2", new Dictionary<string, Value> { ["age"] = new IntegerValue(40) });

        var doc = await m.GetAsync("users/u1");
        Assert.Equal(new IntegerValue(30), doc!.Fields["age"]);

        var q = await m.QueryAsync(new QueryAst("users",
            Where: new FieldFilterAst(F("age"), FilterOperator.Gte, new IntegerValue(35))));
        Assert.Equal("u2", Assert.Single(q.Documents).Id);

        var agg = await m.AggregateAsync(new PipelineAst([
            new MatchStageAst("users", null),
            new GroupStageAst([], [new AccumulatorAst("n", AggFunction.Count)])]));
        Assert.Equal(new IntegerValue(2), Assert.Single(agg.Rows)["n"]);

        Assert.True(await m.DeleteAsync("users/u1"));
        Assert.Null(await m.GetAsync("users/u1"));
    }

    [Fact]
    public async Task CommitAndSync_Batches()
    {
        var m = Manager();
        var commit = await m.CommitAsync(new OperationBatch
        {
            Operations =
            [
                new BatchOperation { Type = BatchOperationType.Set, Path = "c/a",
                    Fields = new Dictionary<string, Value> { ["x"] = new IntegerValue(1) } },
                new BatchOperation { Type = BatchOperationType.Update, Path = "c/a",
                    Fields = new Dictionary<string, Value> { ["y"] = new IntegerValue(2) } },
            ],
        });
        Assert.Equal(2, commit.Documents.Count);
        var doc = await m.GetAsync("c/a");
        Assert.Equal(2, doc!.Fields.Count);

        var sync = await m.SyncAsync(new MutationBatch
        {
            Path = "c/a",
            Mutations = [new Mutation { Type = MutationType.Update,
                Fields = new Dictionary<string, Value> { ["z"] = new IntegerValue(3) } }],
        });
        Assert.False(sync.HasConflict);
        Assert.Equal(new IntegerValue(3), sync.Document!.Fields["z"]);
    }
}
