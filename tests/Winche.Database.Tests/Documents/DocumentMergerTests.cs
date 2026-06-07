using Winche.Database.Documents;
using Winche.Database.Values;

namespace Winche.Database.Tests.Documents;

public class DocumentMergerTests
{
    private static Dictionary<string, Value> Map(params (string Key, Value Value)[] entries) =>
        entries.ToDictionary(e => e.Key, e => e.Value);

    [Fact]
    public void ScalarPatch_Replaces()
    {
        var result = DocumentMerger.Merge(
            Map(("a", new IntegerValue(1))),
            Map(("a", new IntegerValue(2))));
        Assert.Equal(new IntegerValue(2), result["a"]);
    }

    [Fact]
    public void TargetOnlyKeys_AreKept()
    {
        var result = DocumentMerger.Merge(
            Map(("keep", new StringValue("old")), ("change", new IntegerValue(1))),
            Map(("change", new IntegerValue(2))));
        Assert.Equal(new StringValue("old"), result["keep"]);
        Assert.Equal(new IntegerValue(2), result["change"]);
    }

    [Fact]
    public void PatchOnlyKeys_AreAdded()
    {
        var result = DocumentMerger.Merge(
            Map(("a", new IntegerValue(1))),
            Map(("b", new IntegerValue(2))));
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void NestedMaps_MergeRecursively()
    {
        var target = Map(("address", new MapValue(Map(
            ("city", new StringValue("Oslo")),
            ("zip", new StringValue("0001"))))));
        var patch = Map(("address", new MapValue(Map(
            ("city", new StringValue("Bergen"))))));

        var result = DocumentMerger.Merge(target, patch);

        var address = Assert.IsType<MapValue>(result["address"]);
        Assert.Equal(new StringValue("Bergen"), address.Fields["city"]);
        Assert.Equal(new StringValue("0001"), address.Fields["zip"]); // sibling preserved
    }

    [Fact]
    public void MapReplacedByScalar_Replaces()
    {
        var result = DocumentMerger.Merge(
            Map(("x", new MapValue(Map(("y", new IntegerValue(1)))))),
            Map(("x", new StringValue("flat"))));
        Assert.Equal(new StringValue("flat"), result["x"]);
    }

    [Fact]
    public void NullValue_ExplicitlySetsNull()
    {
        var result = DocumentMerger.Merge(
            Map(("a", new IntegerValue(1))),
            Map(("a", new NullValue())));
        Assert.Equal(new NullValue(), result["a"]);
    }

    [Fact]
    public void ArraysReplaceWholesale_NoElementMerge()
    {
        var result = DocumentMerger.Merge(
            Map(("tags", new ArrayValue([new StringValue("a"), new StringValue("b")]))),
            Map(("tags", new ArrayValue([new StringValue("c")]))));
        Assert.Equal(new ArrayValue([new StringValue("c")]), result["tags"]);
    }

    [Fact]
    public void Inputs_AreNotMutated()
    {
        var target = Map(("a", new IntegerValue(1)));
        var patch = Map(("b", new IntegerValue(2)));
        DocumentMerger.Merge(target, patch);
        Assert.Single(target);
        Assert.Single(patch);
    }
}
