using Microsoft.Extensions.Options;
using System.Text.Json.Nodes;
using Winche.Database.Documents;
using Winche.Database.DependencyInjection;
using Winche.Database.Querying.Ast;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;
using Winche.Sentinel.Interfaces;
using Winche.Sentinel.Models;

namespace Winche.Database.IntegrationTests;

/// <summary>Denies the configured (operation, path-prefix) pairs; allows everything else.</summary>
internal sealed class DenyListEvaluator : IAccessRuleEvaluator<Document>
{
    public readonly List<(AccessOperation Op, string Prefix)> Deny = [];
    public Task EvaluateAsync(AccessOperation operation, string path, object? data,
        Func<CancellationToken, Task<Document?>>? getResource, CancellationToken ct = default)
    {
        if (Deny.Any(d => d.Op == operation && path.StartsWith(d.Prefix, StringComparison.Ordinal)))
            throw new AccessDeniedException(operation, path);
        return Task.CompletedTask;
    }
}

[Collection("postgres")]
public class GuardedDatabaseTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private void CreateDb(
        out GuardedDocumentDatabase guarded,
        out DocumentDatabase core,
        out DenyListEvaluator rules)
    {
        core = new DocumentDatabase(Fx.DataSource, Options.Create(new WincheDatabaseOptions()));
        rules = new DenyListEvaluator();
        guarded = new GuardedDocumentDatabase(core, rules);
    }

    private static Dictionary<string, Value> Map(params (string K, Value V)[] e) => e.ToDictionary(x => x.K, x => x.V);

    [Fact]
    public async Task Get_DeniedRead_Throws_CoreStillWorks()
    {
        CreateDb(out var guarded, out var core, out var rules);
        await core.WriteAsync([new SetWrite { Path = "secret/a", Fields = Map() }]);
        rules.Deny.Add((AccessOperation.Read, "secret/"));

        await Assert.ThrowsAsync<AccessDeniedException>(() => guarded.GetAsync("secret/a"));
        Assert.NotNull(await core.GetAsync("secret/a"));                  // the core IS the unprotected API
    }

    [Fact]
    public async Task Query_PostFiltersDeniedDocs()
    {
        CreateDb(out var guarded, out var core, out var rules);
        await core.WriteAsync(
        [
            new SetWrite { Path = "mixed/visible", Fields = Map() },
            new SetWrite { Path = "mixed/hidden", Fields = Map() },
        ]);
        rules.Deny.Add((AccessOperation.Read, "mixed/hidden"));

        var result = await guarded.QueryAsync(new QueryAst("mixed"));
        Assert.Equal("visible", Assert.Single(result.Documents).Id);
    }

    [Fact]
    public async Task Writes_GuardedPerWrite_BatchRejectedBeforeAnyApply()
    {
        CreateDb(out var guarded, out var core, out var rules);
        rules.Deny.Add((AccessOperation.Write, "locked/"));

        await Assert.ThrowsAsync<AccessDeniedException>(() => guarded.WriteAsync(
        [
            new SetWrite { Path = "open/a", Fields = Map() },
            new SetWrite { Path = "locked/b", Fields = Map() },
        ]));
        Assert.Null(await core.GetAsync("open/a"));                       // nothing applied

        rules.Deny.Add((AccessOperation.Delete, "open/"));
        await guarded.WriteAsync([new SetWrite { Path = "open/a", Fields = Map() }]);
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            guarded.WriteAsync([new DeleteWrite { Path = "open/a" }]));
    }

    [Fact]
    public async Task Aggregate_GatesCollections()
    {
        CreateDb(out var guarded, out _, out var rules);
        rules.Deny.Add((AccessOperation.Read, "private"));
        await Assert.ThrowsAsync<AccessDeniedException>(() => guarded.AggregateAsync(new PipelineAst(
            [new MatchStageAst("private", null)])));
    }

    [Fact]
    public async Task RunTransaction_GoesThroughGuard()
    {
        CreateDb(out var guarded, out var core, out var rules);
        await core.WriteAsync([new SetWrite { Path = "secret/a", Fields = Map() }]);
        rules.Deny.Add((AccessOperation.Read, "secret/"));

        await Assert.ThrowsAsync<AccessDeniedException>(() => guarded.RunTransactionAsync<bool>(async ctx =>
        {
            await ctx.GetAsync("secret/a");
            return true;
        }));
        Assert.Equal(0, core.Ledger.Count);                               // rolled back, no leak
    }
}
