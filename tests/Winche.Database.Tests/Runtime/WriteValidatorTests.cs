// tests/Winche.Database.Tests/Runtime/WriteValidatorTests.cs
using Winche.Database.Documents;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.Tests.Runtime;

public class WriteValidatorTests
{
    private static FieldPath F(string p) => FieldPath.Parse(p);
    private static Dictionary<string, Value> Fields(params (string K, Value V)[] e) => e.ToDictionary(x => x.K, x => x.V);

    private static RuntimeException Throws(params Write[] writes)
    {
        var ex = Assert.Throws<RuntimeException>(() => WriteValidator.Validate(writes));
        Assert.Equal(RuntimeStatus.InvalidArgument, ex.Status);
        return ex;
    }

    [Fact]
    public void ValidWrites_Pass()
    {
        WriteValidator.Validate(
        [
            new SetWrite { Path = "c/a", Fields = Fields(("x", new IntegerValue(1))) },
            new SetWrite { Path = "c/b", Fields = Fields(("x", DeleteFieldValue.Instance)), Merge = true },
            new UpdateWrite { Path = "c/a", Fields = new Dictionary<FieldPath, Value> { [F("x")] = DeleteFieldValue.Instance } },
            new UpdateWrite { Path = "c/a", Fields = new Dictionary<FieldPath, Value>(),
                Transforms = [new FieldTransform(F("n"), TransformKind.Increment, new IntegerValue(1))] },
            new DeleteWrite { Path = "c/a" },
            new DeleteWrite { Path = "c", Cascade = true },     // cascade may target a collection path
        ]);
    }

    [Fact]
    public void EmptyOrOversizedBatch_Throws()
    {
        Throws();
        var many = Enumerable.Range(0, 501).Select(i => (Write)new DeleteWrite { Path = $"c/d{i}" }).ToArray();
        Throws(many);
    }

    [Fact]
    public void NonDocumentPath_Throws_ExceptCascadeDelete()
    {
        Throws(new SetWrite { Path = "c", Fields = Fields(("x", new IntegerValue(1))) });
        Throws(new DeleteWrite { Path = "c" });                  // non-cascade delete needs a document path
    }

    [Fact]
    public void Sentinel_InNonMergeSet_Throws() =>
        Throws(new SetWrite { Path = "c/a", Fields = Fields(("x", DeleteFieldValue.Instance)) });

    [Fact]
    public void Sentinel_Nested_Throws()
    {
        Throws(new SetWrite { Path = "c/a", Merge = true,
            Fields = Fields(("m", new MapValue(Fields(("x", DeleteFieldValue.Instance))))) });
        Throws(new UpdateWrite { Path = "c/a", Fields = new Dictionary<FieldPath, Value>
            { [F("a")] = new ArrayValue([DeleteFieldValue.Instance]) } });
    }

    [Fact]
    public void UpdateWithNoFieldsAndNoTransforms_Throws() =>
        Throws(new UpdateWrite { Path = "c/a", Fields = new Dictionary<FieldPath, Value>() });

    [Fact]
    public void DuplicateTransformField_Throws() =>
        Throws(new UpdateWrite { Path = "c/a", Fields = new Dictionary<FieldPath, Value>(),
            Transforms =
            [
                new FieldTransform(F("n"), TransformKind.Increment, new IntegerValue(1)),
                new FieldTransform(F("n"), TransformKind.Maximum, new IntegerValue(5)),
            ] });

    [Fact]
    public void TransformOperandShapes_Validated()
    {
        Throws(new SetWrite { Path = "c/a", Fields = Fields(),
            Transforms = [new FieldTransform(F("n"), TransformKind.Increment, new StringValue("1"))] });
        Throws(new SetWrite { Path = "c/a", Fields = Fields(),
            Transforms = [new FieldTransform(F("n"), TransformKind.ArrayUnion, new IntegerValue(1))] });
        Throws(new SetWrite { Path = "c/a", Fields = Fields(),
            Transforms = [new FieldTransform(F("n"), TransformKind.ServerTimestamp, new IntegerValue(1))] });
        Throws(new SetWrite { Path = "c/a", Fields = Fields(),
            Transforms = [new FieldTransform(F("n"), TransformKind.Maximum, null)] });
    }

    [Fact]
    public void SentinelAsTransformOperand_Throws() =>
        Throws(new SetWrite { Path = "c/a", Fields = Fields(),
            Transforms = [new FieldTransform(F("n"), TransformKind.ArrayUnion, new ArrayValue([DeleteFieldValue.Instance]))] });

    [Fact]
    public void PreconditionWithBothFieldsNull_Throws() =>
        Throws(new DeleteWrite { Path = "c/a", Precondition = new Precondition() });
}
