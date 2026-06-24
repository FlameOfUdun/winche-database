using Winche.Database.Documents;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.Tests.Runtime;

public class FieldValueTests
{
    [Fact]
    public void ServerTimestamp_BuildsTransform()
    {
        var t = FieldValue.ServerTimestamp("updatedAt");
        Assert.Equal(FieldPath.Parse("updatedAt"), t.Field);
        Assert.Equal(TransformKind.ServerTimestamp, t.Kind);
        Assert.Null(t.Operand);
    }

    [Fact]
    public void Increment_Long_WrapsIntegerValue()
    {
        var t = FieldValue.Increment("n", 3L);
        Assert.Equal(TransformKind.Increment, t.Kind);
        Assert.Equal(new IntegerValue(3), t.Operand);
    }

    [Fact]
    public void Increment_Double_WrapsDoubleValue() =>
        Assert.Equal(new DoubleValue(1.5), FieldValue.Increment("n", 1.5).Operand);

    [Fact]
    public void MaximumMinimum_WrapNumeric()
    {
        Assert.Equal(new IntegerValue(5), FieldValue.Maximum("h", 5L).Operand);
        Assert.Equal(new DoubleValue(2.0), FieldValue.Minimum("l", 2.0).Operand);
    }

    [Fact]
    public void ArrayUnion_WrapsElementsInArrayValue()
    {
        var t = FieldValue.ArrayUnion("tags", new StringValue("a"), new StringValue("b"));
        Assert.Equal(TransformKind.ArrayUnion, t.Kind);
        Assert.Equal(new ArrayValue([new StringValue("a"), new StringValue("b")]), t.Operand);
    }

    [Fact]
    public void ArrayRemove_WrapsElementsInArrayValue()
    {
        var t = FieldValue.ArrayRemove("tags", new StringValue("a"));
        Assert.Equal(TransformKind.ArrayRemove, t.Kind);
        Assert.Equal(new ArrayValue([new StringValue("a")]), t.Operand);
    }

    [Fact]
    public void DottedField_Parsed() =>
        Assert.Equal(FieldPath.Parse("stats.count"), FieldValue.Increment("stats.count", 1L).Field);

    [Fact]
    public void Delete_ReturnsSentinel() =>
        Assert.Same(DeleteFieldValue.Instance, FieldValue.Delete());

    [Fact]
    public void Helpers_ProduceValidatorAcceptedTransforms() =>
        WriteValidator.Validate(
        [
            new UpdateWrite
            {
                Path = "c/a",
                Fields = new Dictionary<FieldPath, Value>(),
                Transforms =
                [
                    FieldValue.ServerTimestamp("t"),
                    FieldValue.Increment("n", 1L),
                    FieldValue.ArrayUnion("a", new StringValue("x")),
                ],
            },
        ]);
}
