using Winche.Database.Runtime;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.Tests.Runtime;

public class DocumentLimitsTests
{
    private static Dictionary<string, Value> Map(params (string K, Value V)[] e) => e.ToDictionary(x => x.K, x => x.V);

    [Fact]
    public void Size_OverLimit_Throws()
    {
        var limits = new WriteLimits { MaxDocumentSizeBytes = 10 };
        var ex = Assert.Throws<RuntimeException>(() =>
            DocumentLimits.Validate("c/a", Map(("s", new StringValue("a fairly long string value"))), limits));
        Assert.Equal(RuntimeStatus.InvalidArgument, ex.Status);
    }

    [Fact]
    public void Size_WithinLimit_Ok()
    {
        DocumentLimits.Validate("c/a", Map(("n", new IntegerValue(1))), new WriteLimits());
    }

    [Fact]
    public void Depth_AtLimit_Ok_OverLimit_Throws()
    {
        var limits = new WriteLimits { MaxDepth = 3 };
        // root(1) -> a(2) -> b(3, leaf int) : within depth 3
        Value ok = new MapValue(Map(("a", new MapValue(Map(("b", new IntegerValue(1)))))));
        DocumentLimits.Validate("c/a", Map(("root", ok)), limits);

        // root(1) -> a(2) -> b(3) -> c(4) : exceeds depth 3
        Value deep = new MapValue(Map(("a", new MapValue(Map(("b", new MapValue(Map(("c", new IntegerValue(1))))))))));
        Assert.Throws<RuntimeException>(() => DocumentLimits.Validate("c/a", Map(("root", deep)), limits));
    }

    [Fact]
    public void ReservedFieldName_Throws_UnlessDisabled()
    {
        Assert.Throws<RuntimeException>(() =>
            DocumentLimits.Validate("c/a", Map(("__x__", new IntegerValue(1))), new WriteLimits()));

        // disabled → allowed
        DocumentLimits.Validate("c/a", Map(("__x__", new IntegerValue(1))),
            new WriteLimits { RejectReservedFieldNames = false });
    }

    [Fact]
    public void EmptyFieldName_Throws()
    {
        Assert.Throws<RuntimeException>(() =>
            DocumentLimits.Validate("c/a", Map(("", new IntegerValue(1))), new WriteLimits()));
    }

    [Fact]
    public void ArrayNesting_OverDepth_Throws()
    {
        var limits = new WriteLimits { MaxDepth = 3 };
        // root(1) -> array(2) -> array(3) -> array(4) : exceeds depth 3
        Value v = new ArrayValue([new ArrayValue([new ArrayValue([new IntegerValue(1)])])]);
        Assert.Throws<RuntimeException>(() => DocumentLimits.Validate("c/a", Map(("root", v)), limits));
    }

    [Fact]
    public void BytesValue_CountsTowardSize()
    {
        var limits = new WriteLimits { MaxDocumentSizeBytes = 50 };
        var ex = Assert.Throws<RuntimeException>(() =>
            DocumentLimits.Validate("c/a", Map(("b", new BytesValue(new byte[200]))), limits));
        Assert.Equal(RuntimeStatus.InvalidArgument, ex.Status);
    }
}
