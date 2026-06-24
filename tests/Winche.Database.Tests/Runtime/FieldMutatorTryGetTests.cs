using Winche.Database.Documents;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.Tests.Runtime;

public class FieldMutatorTryGetTests
{
    private static Dictionary<string, Value> Map(params (string K, Value V)[] e) => e.ToDictionary(x => x.K, x => x.V);
    private static FieldPath FP(string p) => FieldPath.Parse(p);

    [Fact]
    public void TryGet_TopLevel_Present()
    {
        var fields = Map(("a", new IntegerValue(1)));
        Assert.True(FieldMutator.TryGet(fields, FP("a"), out var v));
        Assert.Equal(new IntegerValue(1), v);
    }

    [Fact]
    public void TryGet_Nested_Present()
    {
        var fields = Map(("m", new MapValue(Map(("x", new StringValue("hi"))))));
        Assert.True(FieldMutator.TryGet(fields, FP("m.x"), out var v));
        Assert.Equal(new StringValue("hi"), v);
    }

    [Fact]
    public void TryGet_MissingSegment_False()
    {
        var fields = Map(("a", new IntegerValue(1)));
        Assert.False(FieldMutator.TryGet(fields, FP("b"), out _));
    }

    [Fact]
    public void TryGet_NonMapMidPath_False()
    {
        var fields = Map(("a", new IntegerValue(1)));
        Assert.False(FieldMutator.TryGet(fields, FP("a.b"), out _));
    }

    [Fact]
    public void TryGet_MissingIntermediate_AtDepth2_False()
    {
        // 'a' is a map but 'b' is absent → descent past the first segment hits a missing key.
        var fields = Map(("a", new MapValue(Map(("x", new IntegerValue(1))))));
        Assert.False(FieldMutator.TryGet(fields, FP("a.b.c"), out _));
    }
}
