using Winche.Database.Documents;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class WriteLimitsTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private static Dictionary<string, Value> Map(params (string K, Value V)[] e) => e.ToDictionary(x => x.K, x => x.V);

    private async Task<Document?> Get(string path)
    {
        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        return await new DocumentOperations(conn, null).GetAsync(path);
    }

    private static async Task<RuntimeException> ThrowsInvalid(WriteApplier applier, SetWrite write)
    {
        var ex = await Assert.ThrowsAsync<RuntimeException>(() => applier.ApplyAsync([write]));
        Assert.Equal(RuntimeStatus.InvalidArgument, ex.Status);
        return ex;
    }

    [Fact]
    public async Task OversizedDocument_Throws()
    {
        var applier = new WriteApplier(Fx.DataSource, null, new WriteLimits { MaxDocumentSizeBytes = 50 });
        await ThrowsInvalid(applier, new SetWrite { Path = "c/big", Fields = Map(("s", new StringValue(new string('x', 500)))) });
    }

    [Fact]
    public async Task TooDeepDocument_Throws()
    {
        // default MaxDepth is 20; 25 nested maps (depth 26 at the leaf) exceeds it.
        Value v = new IntegerValue(1);
        for (var i = 0; i < 25; i++) v = new MapValue(Map(("a", v)));
        var applier = new WriteApplier(Fx.DataSource);
        await ThrowsInvalid(applier, new SetWrite { Path = "c/deep", Fields = Map(("root", v)) });
    }

    [Fact]
    public async Task UpdateWrite_OversizedResult_Throws()
    {
        const string path = "c/upd";
        // seed a small doc under default limits
        await new WriteApplier(Fx.DataSource).ApplyAsync(
            [new SetWrite { Path = path, Fields = Map(("x", new IntegerValue(1))) }]);

        // update that pushes the resulting document over a tight limit → ApplyUpdate must enforce it
        var applier = new WriteApplier(Fx.DataSource, null, new WriteLimits { MaxDocumentSizeBytes = 200 });
        var ex = await Assert.ThrowsAsync<RuntimeException>(() => applier.ApplyAsync(
        [
            new UpdateWrite
            {
                Path = path,
                Fields = new Dictionary<FieldPath, Value> { [FieldPath.Parse("s")] = new StringValue(new string('y', 500)) },
            },
        ]));
        Assert.Equal(RuntimeStatus.InvalidArgument, ex.Status);
    }

    [Fact]
    public async Task Batch_OneViolatingWrite_AbortsWholeBatch()
    {
        var applier = new WriteApplier(Fx.DataSource, null, new WriteLimits { MaxDocumentSizeBytes = 200 });
        var ex = await Assert.ThrowsAsync<RuntimeException>(() => applier.ApplyAsync(
        [
            new SetWrite { Path = "c/atomicA", Fields = Map(("n", new IntegerValue(1))) },                  // within 200
            new SetWrite { Path = "c/atomicB", Fields = Map(("s", new StringValue(new string('z', 500)))) }, // over 200
        ]));
        Assert.Equal(RuntimeStatus.InvalidArgument, ex.Status);

        // whole-batch rollback: the valid write must NOT have persisted.
        Assert.Null(await Get("c/atomicA"));
    }

    [Fact]
    public async Task ReservedFieldName_Throws()
    {
        var applier = new WriteApplier(Fx.DataSource);
        await ThrowsInvalid(applier, new SetWrite { Path = "c/r", Fields = Map(("__meta__", new IntegerValue(1))) });
    }

    [Fact]
    public async Task WithinLimits_Succeeds()
    {
        var applier = new WriteApplier(Fx.DataSource);
        await applier.ApplyAsync([new SetWrite { Path = "c/ok", Fields = Map(("n", new IntegerValue(1)), ("s", new StringValue("hello"))) }]);
        // no throw
    }

    // ── cross-feature: a mergeFields result is limit-checked post-apply (spec §4) ──

    [Fact]
    public async Task MergeFields_OversizedResult_Throws()
    {
        const string path = "c/mflim";
        await new WriteApplier(Fx.DataSource).ApplyAsync(
            [new SetWrite { Path = path, Fields = Map(("a", new IntegerValue(1))) }]);

        // a mergeFields write whose resulting document exceeds a tight limit → rejected post-apply
        var applier = new WriteApplier(Fx.DataSource, null, new WriteLimits { MaxDocumentSizeBytes = 200 });
        var ex = await Assert.ThrowsAsync<RuntimeException>(() => applier.ApplyAsync(
        [
            new SetWrite
            {
                Path = path,
                Fields = Map(("big", new StringValue(new string('q', 500)))),
                MergeFields = [FieldPath.Parse("big")],
            },
        ]));
        Assert.Equal(RuntimeStatus.InvalidArgument, ex.Status);
    }

    [Fact]
    public async Task MergeFields_WithinLimits_Succeeds()
    {
        const string path = "c/mfok";
        var applier = new WriteApplier(Fx.DataSource, null, new WriteLimits { MaxDocumentSizeBytes = 500 });
        await applier.ApplyAsync(
            [new SetWrite { Path = path, Fields = Map(("a", new IntegerValue(1)), ("b", new IntegerValue(2))) }]);

        // mergeFields update that stays within the limit
        await applier.ApplyAsync(
            [new SetWrite { Path = path, Fields = Map(("a", new IntegerValue(10))), MergeFields = [FieldPath.Parse("a")] }]);

        var doc = await Get(path);
        Assert.Equal(new IntegerValue(10), doc!.Fields["a"]);
        Assert.Equal(new IntegerValue(2), doc.Fields["b"]);
    }
}
