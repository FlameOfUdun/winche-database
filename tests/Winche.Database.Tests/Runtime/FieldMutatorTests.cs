// tests/Winche.Database.Tests/Runtime/FieldMutatorTests.cs
using Winche.Database.Documents;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.Tests.Runtime;

public class FieldMutatorTests
{
    private static FieldPath F(string p) => FieldPath.Parse(p);
    private static Dictionary<string, Value> Map(params (string K, Value V)[] e) => e.ToDictionary(x => x.K, x => x.V);

    [Fact]
    public void Set_TopLevel_AddsAndReplaces()
    {
        var r = FieldMutator.Set(Map(("a", new IntegerValue(1))), F("b"), new IntegerValue(2));
        Assert.Equal(2, r.Count);
        r = FieldMutator.Set(r, F("a"), new IntegerValue(9));
        Assert.Equal(new IntegerValue(9), r["a"]);
    }

    [Fact]
    public void Set_Nested_CreatesIntermediateMaps()
    {
        var r = FieldMutator.Set(Map(), F("a.b.c"), new IntegerValue(1));
        var a = Assert.IsType<MapValue>(r["a"]);
        var b = Assert.IsType<MapValue>(a.Fields["b"]);
        Assert.Equal(new IntegerValue(1), b.Fields["c"]);
    }

    [Fact]
    public void Set_Nested_PreservesSiblings_ReplacesNonMapIntermediate()
    {
        var start = Map(("a", new MapValue(Map(("keep", new IntegerValue(7)), ("b", new StringValue("old"))))));
        var r = FieldMutator.Set(start, F("a.b"), new StringValue("new"));
        var a = Assert.IsType<MapValue>(r["a"]);
        Assert.Equal(new IntegerValue(7), a.Fields["keep"]);
        Assert.Equal(new StringValue("new"), a.Fields["b"]);

        // non-map intermediate is replaced by a map (Firestore update behavior)
        var r2 = FieldMutator.Set(Map(("a", new IntegerValue(1))), F("a.b"), new IntegerValue(2));
        Assert.IsType<MapValue>(r2["a"]);
    }

    [Fact]
    public void Delete_TopLevelAndNested()
    {
        var start = Map(
            ("x", new IntegerValue(1)),
            ("a", new MapValue(Map(("b", new IntegerValue(2)), ("keep", new IntegerValue(3))))));
        var r = FieldMutator.Delete(start, F("x"));
        Assert.False(r.ContainsKey("x"));
        r = FieldMutator.Delete(r, F("a.b"));
        var a = Assert.IsType<MapValue>(r["a"]);
        Assert.False(a.Fields.ContainsKey("b"));
        Assert.True(a.Fields.ContainsKey("keep"));
    }

    [Fact]
    public void Delete_MissingPath_IsNoOp()
    {
        var start = Map(("a", new IntegerValue(1)));
        Assert.Equal(start, FieldMutator.Delete(start, F("zz")));
        Assert.Equal(start, FieldMutator.Delete(start, F("a.b.c")));   // a is not a map → nothing to delete
    }

    [Fact]
    public void Inputs_NotMutated()
    {
        var start = Map(("a", new IntegerValue(1)));
        FieldMutator.Set(start, F("b"), new IntegerValue(2));
        FieldMutator.Delete(start, F("a"));
        Assert.Single(start);
    }
}
