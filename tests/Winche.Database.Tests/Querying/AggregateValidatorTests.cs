using Winche.Database.Documents;
using Winche.Database.Querying;
using Winche.Database.Runtime;

namespace Winche.Database.Tests.Querying;

public class AggregateValidatorTests
{
    private static FieldPath FP(string p) => FieldPath.Parse(p);

    private static void AssertInvalid(Action act)
    {
        var ex = Assert.Throws<RuntimeException>(act);
        Assert.Equal(RuntimeStatus.InvalidArgument, ex.Status);
    }

    [Fact] public void Empty_Throws() => AssertInvalid(() => AggregateValidator.Validate([]));

    [Fact]
    public void TooMany_Throws() => AssertInvalid(() => AggregateValidator.Validate(
    [
        Aggregation.Count("a"), Aggregation.Count("b"), Aggregation.Count("c"),
        Aggregation.Count("d"), Aggregation.Count("e"), Aggregation.Count("f"),
    ]));

    [Fact] public void SumWithoutField_Throws() =>
        AssertInvalid(() => AggregateValidator.Validate([new Aggregation(AggregateKind.Sum, "s")]));

    [Fact] public void CountWithField_Throws() =>
        AssertInvalid(() => AggregateValidator.Validate([new Aggregation(AggregateKind.Count, "c", FP("x"))]));

    [Fact] public void NameField_Throws() =>
        AssertInvalid(() => AggregateValidator.Validate([Aggregation.Sum("__name__", "s")]));

    [Fact] public void DuplicateAlias_Throws() =>
        AssertInvalid(() => AggregateValidator.Validate([Aggregation.Count("a"), Aggregation.Sum("x", "a")]));

    [Fact] public void AverageWithoutField_Throws() =>
        AssertInvalid(() => AggregateValidator.Validate([new Aggregation(AggregateKind.Average, "a")]));

    [Fact]
    public void Valid_Passes() => AggregateValidator.Validate(
        [Aggregation.Count("c"), Aggregation.Sum("x", "s"), Aggregation.Average("y", "a")]);

    [Fact]
    public void ExactlyFive_Passes() => AggregateValidator.Validate(
    [
        Aggregation.Count("a"), Aggregation.Sum("w", "b"), Aggregation.Average("x", "c"),
        Aggregation.Sum("y", "d"), Aggregation.Average("z", "e"),
    ]);
}
