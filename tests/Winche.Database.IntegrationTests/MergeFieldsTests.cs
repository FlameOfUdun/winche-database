using Winche.Database.Documents;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class MergeFieldsTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private WriteApplier Applier() => new(Fx.DataSource);
    private static FieldPath FP(string p) => FieldPath.Parse(p);
    private static Dictionary<string, Value> Map(params (string K, Value V)[] e) => e.ToDictionary(x => x.K, x => x.V);

    private async Task<Document?> Get(string path)
    {
        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        return await new DocumentOperations(conn, null).GetAsync(path);
    }

    [Fact]
    public async Task MergeFields_SetsMasked_LeavesUnmasked_DeletesMaskedAbsent()
    {
        var applier = Applier();
        await applier.ApplyAsync([new SetWrite
        {
            Path = "c/a",
            Fields = Map(("a", new IntegerValue(1)), ("b", new IntegerValue(2)), ("c", new IntegerValue(3))),
        }]);

        // mask [a, c], data {a:10} → a:=10 (masked+present), c deleted (masked+absent), b untouched (unmasked)
        await applier.ApplyAsync([new SetWrite
        {
            Path = "c/a",
            Fields = Map(("a", new IntegerValue(10))),
            MergeFields = [FP("a"), FP("c")],
        }]);

        var doc = await Get("c/a");
        Assert.Equal(new IntegerValue(10), doc!.Fields["a"]);
        Assert.Equal(new IntegerValue(2), doc.Fields["b"]);
        Assert.False(doc.Fields.ContainsKey("c"));
    }

    [Fact]
    public async Task MergeFields_NestedMask_UpdatesOnlyMaskedLeaf()
    {
        var applier = Applier();
        await applier.ApplyAsync([new SetWrite
        {
            Path = "c/b",
            Fields = Map(("m", new MapValue(Map(("x", new IntegerValue(1)), ("y", new IntegerValue(2)))))),
        }]);

        // mask [m.x], data {m:{x:9}} → only m.x updated, m.y untouched
        await applier.ApplyAsync([new SetWrite
        {
            Path = "c/b",
            Fields = Map(("m", new MapValue(Map(("x", new IntegerValue(9)))))),
            MergeFields = [FP("m.x")],
        }]);

        var doc = await Get("c/b");
        var m = (MapValue)doc!.Fields["m"];
        Assert.Equal(new IntegerValue(9), m.Fields["x"]);
        Assert.Equal(new IntegerValue(2), m.Fields["y"]);
    }

    [Fact]
    public async Task MergeFields_OnAbsentDocument_CreatesWithMaskedFieldsOnly()
    {
        var applier = Applier();
        // no prior document at c/new; mask [a], data {a:1, b:2} → creates {a:1} (b unmasked, ignored)
        await applier.ApplyAsync([new SetWrite
        {
            Path = "c/new",
            Fields = Map(("a", new IntegerValue(1)), ("b", new IntegerValue(2))),
            MergeFields = [FP("a")],
        }]);

        var doc = await Get("c/new");
        Assert.NotNull(doc);
        Assert.Equal(new IntegerValue(1), doc!.Fields["a"]);
        Assert.False(doc.Fields.ContainsKey("b"));
    }

    [Fact]
    public async Task MergeFields_DeleteSentinelAtMaskedPath_RemovesField()
    {
        var applier = Applier();
        await applier.ApplyAsync([new SetWrite
        {
            Path = "c/del",
            Fields = Map(("a", new IntegerValue(1)), ("b", new IntegerValue(2))),
        }]);

        // mask [a], data {a: deleteField} → a removed, b untouched
        await applier.ApplyAsync([new SetWrite
        {
            Path = "c/del",
            Fields = Map(("a", DeleteFieldValue.Instance)),
            MergeFields = [FP("a")],
        }]);

        var doc = await Get("c/del");
        Assert.False(doc!.Fields.ContainsKey("a"));
        Assert.Equal(new IntegerValue(2), doc.Fields["b"]);
    }

    [Fact]
    public async Task MergeFields_WholeNodeMask_ReplacesEntireSubtree()
    {
        var applier = Applier();
        await applier.ApplyAsync([new SetWrite
        {
            Path = "c/node",
            Fields = Map(("m", new MapValue(Map(("x", new IntegerValue(1)), ("y", new IntegerValue(2)))))),
        }]);

        // mask [m] (intermediate node), data {m:{z:9}} → entire m replaced (x and y gone)
        await applier.ApplyAsync([new SetWrite
        {
            Path = "c/node",
            Fields = Map(("m", new MapValue(Map(("z", new IntegerValue(9)))))),
            MergeFields = [FP("m")],
        }]);

        var doc = await Get("c/node");
        var m = (MapValue)doc!.Fields["m"];
        Assert.Equal(new IntegerValue(9), m.Fields["z"]);
        Assert.False(m.Fields.ContainsKey("x"));
        Assert.False(m.Fields.ContainsKey("y"));
    }
}
