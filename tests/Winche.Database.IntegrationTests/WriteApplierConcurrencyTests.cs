using Winche.Database.Documents;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class WriteApplierConcurrencyTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private WriteApplier Applier() => new(Fx.DataSource);
    private static FieldPath FP(string p) => FieldPath.Parse(p);

    private async Task<Document?> Get(string path)
    {
        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        return await new DocumentOperations(conn, null).GetAsync(path);
    }

    [Fact]
    public async Task ConcurrentIncrements_SumExactly()
    {
        await Applier().ApplyAsync([new SetWrite { Path = "c/counter",
            Fields = new Dictionary<string, Value> { ["n"] = new IntegerValue(0) } }]);

        const int N = 20;
        await Task.WhenAll(Enumerable.Range(0, N).Select(_ => Applier().ApplyAsync(
            [new UpdateWrite { Path = "c/counter", Fields = new Dictionary<FieldPath, Value>(),
                Transforms = [new FieldTransform(FP("n"), TransformKind.Increment, new IntegerValue(1))] }])));

        var doc = await Get("c/counter");
        Assert.Equal(new IntegerValue(N), doc!.Fields["n"]);
        Assert.Equal(N + 1, doc.Version);
    }

    [Fact]
    public async Task ConcurrentMultiDocBatches_NoDeadlock()
    {
        // opposite-order targets in each batch; sorted locking must prevent deadlock
        await Applier().ApplyAsync(
        [
            new SetWrite { Path = "c/a", Fields = new Dictionary<string, Value> { ["n"] = new IntegerValue(0) } },
            new SetWrite { Path = "c/b", Fields = new Dictionary<string, Value> { ["n"] = new IntegerValue(0) } },
        ]);

        const int N = 10;
        var ab = Enumerable.Range(0, N).Select(_ => Applier().ApplyAsync(
        [
            new UpdateWrite { Path = "c/a", Fields = new Dictionary<FieldPath, Value>(),
                Transforms = [new FieldTransform(FP("n"), TransformKind.Increment, new IntegerValue(1))] },
            new UpdateWrite { Path = "c/b", Fields = new Dictionary<FieldPath, Value>(),
                Transforms = [new FieldTransform(FP("n"), TransformKind.Increment, new IntegerValue(1))] },
        ]));
        var ba = Enumerable.Range(0, N).Select(_ => Applier().ApplyAsync(
        [
            new UpdateWrite { Path = "c/b", Fields = new Dictionary<FieldPath, Value>(),
                Transforms = [new FieldTransform(FP("n"), TransformKind.Increment, new IntegerValue(1))] },
            new UpdateWrite { Path = "c/a", Fields = new Dictionary<FieldPath, Value>(),
                Transforms = [new FieldTransform(FP("n"), TransformKind.Increment, new IntegerValue(1))] },
        ]));

        await Task.WhenAll(ab.Concat(ba));

        Assert.Equal(new IntegerValue(2 * N), (await Get("c/a"))!.Fields["n"]);
        Assert.Equal(new IntegerValue(2 * N), (await Get("c/b"))!.Fields["n"]);
    }

    [Fact]
    public async Task ReadValidation_StaleRead_Aborts()
    {
        await Applier().ApplyAsync([new SetWrite { Path = "c/a",
            Fields = new Dictionary<string, Value> { ["x"] = new IntegerValue(1) } }]);
        var readTime = (await Get("c/a"))!.UpdateTime;

        // someone else writes in between
        await Applier().ApplyAsync([new SetWrite { Path = "c/a",
            Fields = new Dictionary<string, Value> { ["x"] = new IntegerValue(2) } }]);

        var ex = await Assert.ThrowsAsync<RuntimeException>(() => Applier().ApplyAsync(
            [new SetWrite { Path = "c/a", Fields = new Dictionary<string, Value> { ["x"] = new IntegerValue(3) } }],
            readValidations: new Dictionary<string, DateTimeOffset?> { ["c/a"] = readTime }));
        Assert.Equal(RuntimeStatus.Aborted, ex.Status);
        Assert.Equal(new IntegerValue(2), (await Get("c/a"))!.Fields["x"]);   // nothing applied
    }

    [Fact]
    public async Task ReadValidation_ReadAsMissing_AbortsWhenCreated()
    {
        await Applier().ApplyAsync([new SetWrite { Path = "c/born",
            Fields = new Dictionary<string, Value>() }]);

        var ex = await Assert.ThrowsAsync<RuntimeException>(() => Applier().ApplyAsync(
            [new SetWrite { Path = "c/other", Fields = new Dictionary<string, Value>() }],
            readValidations: new Dictionary<string, DateTimeOffset?> { ["c/born"] = null }));
        Assert.Equal(RuntimeStatus.Aborted, ex.Status);
    }

    [Fact]
    public async Task ReadValidation_Fresh_Passes()
    {
        await Applier().ApplyAsync([new SetWrite { Path = "c/a",
            Fields = new Dictionary<string, Value> { ["x"] = new IntegerValue(1) } }]);
        var readTime = (await Get("c/a"))!.UpdateTime;

        await Applier().ApplyAsync(
            [new SetWrite { Path = "c/a", Fields = new Dictionary<string, Value> { ["x"] = new IntegerValue(9) } }],
            readValidations: new Dictionary<string, DateTimeOffset?> { ["c/a"] = readTime, ["c/unread"] = null });
        Assert.Equal(new IntegerValue(9), (await Get("c/a"))!.Fields["x"]);
    }

    // C1 — Concurrent first-creation loses writes / collides versions
    [Fact]
    public async Task ConcurrentFirstCreates_NeverLoseWrites()
    {
        const int N = 8;
        var outcomes = await Task.WhenAll(Enumerable.Range(0, N).Select(async i =>
        {
            try
            {
                await Applier().ApplyAsync([new SetWrite { Path = "c/same",
                    Fields = new Dictionary<string, Value> { ["writer"] = new IntegerValue(i) } }]);
                return true;
            }
            catch (RuntimeException ex) when (ex.Status == RuntimeStatus.Aborted) { return false; }
        }));

        var successes = outcomes.Count(s => s);
        Assert.True(successes >= 1);

        var doc = await Get("c/same");
        Assert.Equal(successes, doc!.Version);                     // every committed write bumped exactly once

        var changes = await Fx.ReadChangesAsync();
        Assert.Equal(successes, changes.Count);
        Assert.Equal(1, changes.Count(c => c.Type == "added"));    // exactly one 'added'
        Assert.Equal(changes.Select(c => c.Version).Order(), Enumerable.Range(1, successes).Select(i => (long)i)); // no collisions
    }

    // C2 — Cascade subtree locks deadlock
    [Fact]
    public async Task InterleavedCascades_NoDeadlock()
    {
        await Applier().ApplyAsync(
        [
            new SetWrite { Path = "a/p", Fields = new Dictionary<string, Value>() },
            new SetWrite { Path = "a/p/s/1", Fields = new Dictionary<string, Value>() },
            new SetWrite { Path = "a/q", Fields = new Dictionary<string, Value>() },
            new SetWrite { Path = "a/q/s/1", Fields = new Dictionary<string, Value>() },
        ]);

        for (var round = 0; round < 5; round++)
        {
            var t1 = Applier().ApplyAsync(
            [
                new DeleteWrite { Path = "a/p", Cascade = true },
                new SetWrite { Path = "a/q/s/1", Fields = new Dictionary<string, Value>() },
            ]);
            var t2 = Applier().ApplyAsync(
            [
                new DeleteWrite { Path = "a/q", Cascade = true },
                new SetWrite { Path = "a/p/s/1", Fields = new Dictionary<string, Value>() },
            ]);
            await Task.WhenAll(Swallow(t1), Swallow(t2));          // ABORTED is acceptable; deadlock/unhandled is not

            // reseed for next round
            await Applier().ApplyAsync(
            [
                new SetWrite { Path = "a/p", Fields = new Dictionary<string, Value>() },
                new SetWrite { Path = "a/p/s/1", Fields = new Dictionary<string, Value>() },
                new SetWrite { Path = "a/q", Fields = new Dictionary<string, Value>() },
                new SetWrite { Path = "a/q/s/1", Fields = new Dictionary<string, Value>() },
            ]);
        }

        static async Task Swallow(Task t)
        {
            try { await t; }
            catch (RuntimeException ex) when (ex.Status == RuntimeStatus.Aborted) { }
        }
    }

    [Fact]
    public async Task ReadOnlyValidation_FreshPasses_StaleAborts()
    {
        await Applier().ApplyAsync([new SetWrite { Path = "c/ro",
            Fields = new Dictionary<string, Value> { ["x"] = new IntegerValue(1) } }]);
        var readTime = (await Get("c/ro"))!.UpdateTime;

        // fresh read-only commit: no writes, validations pass, returns empty results
        var results = await Applier().ApplyAsync([],
            readValidations: new Dictionary<string, DateTimeOffset?> { ["c/ro"] = readTime });
        Assert.Empty(results);

        await Applier().ApplyAsync([new SetWrite { Path = "c/ro",
            Fields = new Dictionary<string, Value> { ["x"] = new IntegerValue(2) } }]);

        var ex = await Assert.ThrowsAsync<RuntimeException>(() => Applier().ApplyAsync([],
            readValidations: new Dictionary<string, DateTimeOffset?> { ["c/ro"] = readTime }));
        Assert.Equal(RuntimeStatus.Aborted, ex.Status);
    }

    [Fact]
    public async Task EmptyBatch_WithoutValidations_StillInvalid()
    {
        var ex = await Assert.ThrowsAsync<RuntimeException>(() => Applier().ApplyAsync([]));
        Assert.Equal(RuntimeStatus.InvalidArgument, ex.Status);
    }
}
