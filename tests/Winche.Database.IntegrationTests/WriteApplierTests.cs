using Winche.Database.Documents;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class WriteApplierTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private WriteApplier Applier() => new(Fx.DataSource);
    private static FieldPath FP(string p) => FieldPath.Parse(p);
    private static Dictionary<string, Value> Map(params (string K, Value V)[] e) => e.ToDictionary(x => x.K, x => x.V);

    private async Task<Document?> Get(string path)
    {
        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        return await new DocumentOperations(conn, null).GetAsync(path);
    }

    private static async Task<RuntimeException> ThrowsStatus(RuntimeStatus status, Func<Task> act)
    {
        var ex = await Assert.ThrowsAsync<RuntimeException>(act);
        Assert.Equal(status, ex.Status);
        return ex;
    }

    [Fact]
    public async Task Set_CreatesDocument_AddedChangeRow()
    {
        var results = await Applier().ApplyAsync([new SetWrite { Path = "c/a", Fields = Map(("x", new IntegerValue(1))) }]);
        var doc = await Get("c/a");
        Assert.Equal(new IntegerValue(1), doc!.Fields["x"]);
        Assert.Equal(1, doc.Version);
        Assert.Equal(doc.UpdateTime, results[0].UpdateTime);

        var change = Assert.Single(await Fx.ReadChangesAsync());
        Assert.Equal(("added", "c/a", "c", 1L), (change.Type, change.Path, change.Collection, change.Version));
        Assert.Equal(doc.UpdateTime, change.CommitTime);
    }

    [Fact]
    public async Task Set_Replaces_MergeMerges_SentinelDeletes()
    {
        await Applier().ApplyAsync([new SetWrite { Path = "c/a",
            Fields = Map(("keep", new IntegerValue(1)), ("drop", new IntegerValue(2)), ("x", new IntegerValue(3))) }]);

        // replace: only the new fields remain
        await Applier().ApplyAsync([new SetWrite { Path = "c/a", Fields = Map(("only", new IntegerValue(9))) }]);
        Assert.Single((await Get("c/a"))!.Fields);

        // merge + sentinel
        await Applier().ApplyAsync([new SetWrite { Path = "c/a", Merge = true,
            Fields = Map(("added", new IntegerValue(5)), ("only", DeleteFieldValue.Instance)) }]);
        var doc = await Get("c/a");
        Assert.Equal(new IntegerValue(5), doc!.Fields["added"]);
        Assert.False(doc.Fields.ContainsKey("only"));
        Assert.Equal(3, doc.Version);
    }

    [Fact]
    public async Task Update_DottedPaths_DeleteField_ImplicitExists()
    {
        await ThrowsStatus(RuntimeStatus.NotFound, () => Applier().ApplyAsync(
            [new UpdateWrite { Path = "c/missing", Fields = new Dictionary<FieldPath, Value> { [FP("x")] = new IntegerValue(1) } }]));

        await Applier().ApplyAsync([new SetWrite { Path = "c/a",
            Fields = Map(("addr", new MapValue(Map(("city", new StringValue("Oslo")), ("zip", new StringValue("0001"))))), ("gone", new IntegerValue(1))) }]);

        await Applier().ApplyAsync([new UpdateWrite { Path = "c/a", Fields = new Dictionary<FieldPath, Value>
        {
            [FP("addr.city")] = new StringValue("Bergen"),
            [FP("gone")] = DeleteFieldValue.Instance,
            [FP("brand.new.deep")] = new IntegerValue(7),
        } }]);

        var doc = await Get("c/a");
        var addr = Assert.IsType<MapValue>(doc!.Fields["addr"]);
        Assert.Equal(new StringValue("Bergen"), addr.Fields["city"]);
        Assert.Equal(new StringValue("0001"), addr.Fields["zip"]);
        Assert.False(doc.Fields.ContainsKey("gone"));
        Assert.IsType<MapValue>(doc.Fields["brand"]);
    }

    [Fact]
    public async Task Preconditions_Matrix()
    {
        await Applier().ApplyAsync([new SetWrite { Path = "c/a", Fields = Map(("x", new IntegerValue(1))) }]);
        var doc = await Get("c/a");

        await ThrowsStatus(RuntimeStatus.AlreadyExists, () => Applier().ApplyAsync(
            [new SetWrite { Path = "c/a", Fields = Map(), Precondition = new Precondition(Exists: false) }]));
        await ThrowsStatus(RuntimeStatus.NotFound, () => Applier().ApplyAsync(
            [new DeleteWrite { Path = "c/none", Precondition = new Precondition(Exists: true) }]));
        await ThrowsStatus(RuntimeStatus.FailedPrecondition, () => Applier().ApplyAsync(
            [new SetWrite { Path = "c/a", Fields = Map(),
                Precondition = new Precondition(UpdateTime: doc!.UpdateTime.AddTicks(10)) }]));

        // matching updateTime passes
        await Applier().ApplyAsync([new SetWrite { Path = "c/a", Fields = Map(("x", new IntegerValue(2))),
            Precondition = new Precondition(UpdateTime: doc!.UpdateTime) }]);
        Assert.Equal(new IntegerValue(2), (await Get("c/a"))!.Fields["x"]);
    }

    [Fact]
    public async Task Delete_Missing_NoOp_NoChangeRow()
    {
        await Applier().ApplyAsync([new DeleteWrite { Path = "c/none" }]);
        Assert.Empty(await Fx.ReadChangesAsync());
    }

    [Fact]
    public async Task CascadeDelete_RemovedRowPerPath()
    {
        await Applier().ApplyAsync(
        [
            new SetWrite { Path = "o/x", Fields = Map() },
            new SetWrite { Path = "o/x/sub/s1", Fields = Map() },
            new SetWrite { Path = "o/x/sub/s2", Fields = Map() },
            new SetWrite { Path = "o/y", Fields = Map() },
        ]);
        await Fx.ResetChangesAsync();

        await Applier().ApplyAsync([new DeleteWrite { Path = "o/x", Cascade = true }]);

        Assert.Null(await Get("o/x/sub/s1"));
        Assert.NotNull(await Get("o/y"));
        var changes = await Fx.ReadChangesAsync();
        Assert.Equal(3, changes.Count);
        Assert.All(changes, c => Assert.Equal("removed", c.Type));
        Assert.Equal(["o/x", "o/x/sub/s1", "o/x/sub/s2"], changes.Select(c => c.Path).Order());
    }

    [Fact]
    public async Task Batch_SequentialVisibility_NetChangeRow()
    {
        await Applier().ApplyAsync(
        [
            new SetWrite { Path = "c/a", Fields = Map(("x", new IntegerValue(1))) },
            new UpdateWrite { Path = "c/a", Fields = new Dictionary<FieldPath, Value> { [FP("y")] = new IntegerValue(2) } },
        ]);

        var doc = await Get("c/a");
        Assert.Equal(2, doc!.Fields.Count);                       // update saw the in-batch set
        Assert.Equal(2, doc.Version);                             // version bumps per write

        var change = Assert.Single(await Fx.ReadChangesAsync());  // ONE net row
        Assert.Equal(("added", 2L), (change.Type, change.Version));
    }

    [Fact]
    public async Task Batch_Atomicity_NothingAppliedOnFailure()
    {
        await ThrowsStatus(RuntimeStatus.NotFound, () => Applier().ApplyAsync(
        [
            new SetWrite { Path = "c/good", Fields = Map(("x", new IntegerValue(1))) },
            new UpdateWrite { Path = "c/missing", Fields = new Dictionary<FieldPath, Value> { [FP("x")] = new IntegerValue(1) } },
        ]));

        Assert.Null(await Get("c/good"));
        Assert.Empty(await Fx.ReadChangesAsync());
    }

    [Fact]
    public async Task SingleCommitTime_AcrossBatch_AndServerTimestamp()
    {
        var results = await Applier().ApplyAsync(
        [
            new SetWrite { Path = "c/a", Fields = Map(),
                Transforms = [new FieldTransform(FP("at"), TransformKind.ServerTimestamp)] },
            new SetWrite { Path = "c/b", Fields = Map(),
                Transforms = [new FieldTransform(FP("at"), TransformKind.ServerTimestamp)] },
        ]);

        var a = await Get("c/a");
        var b = await Get("c/b");
        Assert.Equal(a!.UpdateTime, b!.UpdateTime);
        Assert.Equal(results[0].UpdateTime, a.UpdateTime);
        Assert.Equal(new TimestampValue(a.UpdateTime), a.Fields["at"]);
        Assert.Equal(a.Fields["at"], b.Fields["at"]);
        Assert.Equal(new TimestampValue(a.UpdateTime), results[0].TransformResults![FP("at")]);
    }

    [Fact]
    public async Task Transforms_EndToEnd()
    {
        await Applier().ApplyAsync([new SetWrite { Path = "c/t",
            Fields = Map(("n", new IntegerValue(10)), ("tags", new ArrayValue([new StringValue("a")]))) }]);

        var results = await Applier().ApplyAsync([new UpdateWrite { Path = "c/t",
            Fields = new Dictionary<FieldPath, Value>(),
            Transforms =
            [
                new FieldTransform(FP("n"), TransformKind.Increment, new IntegerValue(5)),
                new FieldTransform(FP("tags"), TransformKind.ArrayUnion, new ArrayValue([new StringValue("a"), new StringValue("b")])),
                new FieldTransform(FP("hi"), TransformKind.Maximum, new IntegerValue(3)),
            ] }]);

        var doc = await Get("c/t");
        Assert.Equal(new IntegerValue(15), doc!.Fields["n"]);
        Assert.Equal(2, Assert.IsType<ArrayValue>(doc.Fields["tags"]).Values.Count);
        Assert.Equal(new IntegerValue(3), doc.Fields["hi"]);
        Assert.Equal(new IntegerValue(15), results[0].TransformResults![FP("n")]);
    }

    [Fact]
    public async Task SetMerge_NestedSentinel_DeletesNestedKey_PreservesSiblings()
    {
        await Applier().ApplyAsync([new SetWrite { Path = "c/nest", Fields = Map(
            ("m", new MapValue(Map(("drop", new IntegerValue(1)), ("keep", new IntegerValue(2))))),
            ("top", new IntegerValue(3))) }]);

        await Applier().ApplyAsync([new SetWrite { Path = "c/nest", Merge = true, Fields = Map(
            ("m", new MapValue(Map(("drop", DeleteFieldValue.Instance), ("added", new IntegerValue(4)))))) }]);

        var doc = await Get("c/nest");
        var m = Assert.IsType<MapValue>(doc!.Fields["m"]);
        Assert.False(m.Fields.ContainsKey("drop"));
        Assert.Equal(new IntegerValue(2), m.Fields["keep"]);
        Assert.Equal(new IntegerValue(4), m.Fields["added"]);
        Assert.Equal(new IntegerValue(3), doc.Fields["top"]);
    }

    [Fact]
    public async Task SetMerge_NestedSentinel_MapKeyWithDot_IsLiteralSegment()
    {
        await Applier().ApplyAsync([new SetWrite { Path = "c/dotnest", Fields = Map(
            ("m", new MapValue(Map(("a.b", new IntegerValue(1)), ("c", new IntegerValue(2)))))) }]);
        await Applier().ApplyAsync([new SetWrite { Path = "c/dotnest", Merge = true, Fields = Map(
            ("m", new MapValue(Map(("a.b", DeleteFieldValue.Instance))))) }]);
        var m = Assert.IsType<MapValue>((await Get("c/dotnest"))!.Fields["m"]);
        Assert.False(m.Fields.ContainsKey("a.b"));
        Assert.True(m.Fields.ContainsKey("c"));
    }

    // I3 — Merge-set sentinel must remove the LITERAL top-level key (not parse as FieldPath)
    [Fact]
    public async Task SetMerge_Sentinel_DottedKeyIsLiteral()
    {
        await Applier().ApplyAsync([new SetWrite { Path = "c/dotted",
            Fields = Map(("a.b", new IntegerValue(1)), ("a", new MapValue(Map(("b", new IntegerValue(2)))))) }]);

        await Applier().ApplyAsync([new SetWrite { Path = "c/dotted", Merge = true,
            Fields = Map(("a.b", DeleteFieldValue.Instance)) }]);

        var doc = await Get("c/dotted");
        Assert.False(doc!.Fields.ContainsKey("a.b"));               // literal key removed
        var a = Assert.IsType<MapValue>(doc.Fields["a"]);
        Assert.Equal(new IntegerValue(2), a.Fields["b"]);           // nested a.b untouched
    }

    // I5 — Phantom/destructive empty maps from pruned sentinels

    [Fact]
    public async Task SetMerge_PureSentinelMap_DoesNotCreatePhantomMap()
    {
        await Applier().ApplyAsync([new SetWrite { Path = "c/ph", Fields = Map(("top", new IntegerValue(1))) }]);
        await Applier().ApplyAsync([new SetWrite { Path = "c/ph", Merge = true, Fields = Map(
            ("m", new MapValue(Map(("drop", DeleteFieldValue.Instance))))) }]);
        Assert.False((await Get("c/ph"))!.Fields.ContainsKey("m"));
    }

    [Fact]
    public async Task SetMerge_PureSentinelMap_DoesNotDestroyScalar()
    {
        await Applier().ApplyAsync([new SetWrite { Path = "c/sc", Fields = Map(("m", new IntegerValue(5))) }]);
        await Applier().ApplyAsync([new SetWrite { Path = "c/sc", Merge = true, Fields = Map(
            ("m", new MapValue(Map(("drop", DeleteFieldValue.Instance))))) }]);
        Assert.Equal(new IntegerValue(5), (await Get("c/sc"))!.Fields["m"]);   // scalar untouched (idempotent no-op)
    }

    [Fact]
    public async Task SetMerge_LiteralEmptyMap_IsStillWritten()
    {
        await Applier().ApplyAsync([new SetWrite { Path = "c/em", Merge = true, Fields = Map(
            ("m", new MapValue(new Dictionary<string, Value>()))) }]);
        Assert.IsType<MapValue>((await Get("c/em"))!.Fields["m"]);
    }

    // M5 — Semantic pins

    [Fact]
    public async Task SetThenDeleteInBatch_NetsToNothing()
    {
        await Applier().ApplyAsync(
        [
            new SetWrite { Path = "c/ghost", Fields = Map(("x", new IntegerValue(1))) },
            new DeleteWrite { Path = "c/ghost" },
        ]);
        Assert.Null(await Get("c/ghost"));
        Assert.Empty(await Fx.ReadChangesAsync());
    }

    [Fact]
    public async Task UpdateTimePrecondition_OnDocDeletedEarlierInBatch_Fails()
    {
        await Applier().ApplyAsync([new SetWrite { Path = "c/a", Fields = Map(("x", new IntegerValue(1))) }]);
        var t = (await Get("c/a"))!.UpdateTime;

        await ThrowsStatus(RuntimeStatus.FailedPrecondition, () => Applier().ApplyAsync(
        [
            new DeleteWrite { Path = "c/a" },
            new SetWrite { Path = "c/a", Fields = Map(), Precondition = new Precondition(UpdateTime: t) },
        ]));
        Assert.NotNull(await Get("c/a"));                           // atomic: nothing applied
    }
}
