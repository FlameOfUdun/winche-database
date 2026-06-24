using Winche.Database.Documents;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.Tests.Runtime;

public class MergeFieldsValidationTests
{
    private static Dictionary<string, Value> Map(params (string K, Value V)[] e) => e.ToDictionary(x => x.K, x => x.V);
    private static FieldPath FP(string p) => FieldPath.Parse(p);

    [Fact]
    public void MergeFields_WithMergeTrue_Throws()
    {
        var ex = Assert.Throws<RuntimeException>(() => WriteValidator.Validate(
        [
            new SetWrite { Path = "c/a", Fields = Map(("a", new IntegerValue(1))), Merge = true, MergeFields = [FP("a")] },
        ]));
        Assert.Equal(RuntimeStatus.InvalidArgument, ex.Status);
    }

    [Fact]
    public void MergeFields_AllowsDeleteSentinel()
    {
        // A deleteField in a mergeFields set is legal (drives the masked delete) — must not throw.
        WriteValidator.Validate(
        [
            new SetWrite { Path = "c/a", Fields = Map(("a", DeleteFieldValue.Instance)), MergeFields = [FP("a")] },
        ]);
    }

    [Fact]
    public void MergeFields_Empty_Throws()
    {
        var ex = Assert.Throws<RuntimeException>(() => WriteValidator.Validate(
        [
            new SetWrite { Path = "c/a", Fields = Map(("a", new IntegerValue(1))), MergeFields = [] },
        ]));
        Assert.Equal(RuntimeStatus.InvalidArgument, ex.Status);
    }
}
