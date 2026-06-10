using Winche.Database.Authorization;
using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Values;
using Winche.Rules.Expressions;

namespace Winche.Database.Tests.Authorization;

public class QueryToConstraintsTests
{
    private static FieldPath FP(string dotted) => FieldPath.Parse(dotted);

    [Fact]
    public void NullWhere_ProducesEmptyConstraints()
    {
        var query = new Query("users");
        var result = QueryToConstraints.Convert(query);
        Assert.Equal("users", result.Collection);
        Assert.Empty(result.Constraints);
    }

    [Fact]
    public void SingleFieldFilter_Eq_IsMapped()
    {
        var query = new Query("users",
            Where: new FieldFilter(FP("age"), FilterOperator.Eq, new IntegerValue(18)));
        var result = QueryToConstraints.Convert(query);

        Assert.Single(result.Constraints);
        var c = result.Constraints[0];
        Assert.Equal(new[] { "age" }, c.Field);
        Assert.Equal(CompareOp.Eq, c.Op);
        Assert.Equal(18L, c.Value.AsInt);
    }

    [Theory]
    [InlineData(FilterOperator.Ne,  CompareOp.Ne)]
    [InlineData(FilterOperator.Lt,  CompareOp.Lt)]
    [InlineData(FilterOperator.Lte, CompareOp.Le)]
    [InlineData(FilterOperator.Gt,  CompareOp.Gt)]
    [InlineData(FilterOperator.Gte, CompareOp.Ge)]
    public void FilterOperator_MapsToCompareOp(FilterOperator filterOp, CompareOp expected)
    {
        var query = new Query("col",
            Where: new FieldFilter(FP("score"), filterOp, new IntegerValue(100)));
        var result = QueryToConstraints.Convert(query);

        Assert.Single(result.Constraints);
        Assert.Equal(expected, result.Constraints[0].Op);
    }

    [Theory]
    [InlineData(FilterOperator.In)]
    [InlineData(FilterOperator.NotIn)]
    [InlineData(FilterOperator.ArrayContains)]
    [InlineData(FilterOperator.ArrayContainsAny)]
    [InlineData(FilterOperator.ArrayContainsAll)]
    [InlineData(FilterOperator.Contains)]
    [InlineData(FilterOperator.StartsWith)]
    [InlineData(FilterOperator.EndsWith)]
    [InlineData(FilterOperator.Regex)]
    public void UnsupportedOperator_IsSkipped(FilterOperator op)
    {
        var query = new Query("col",
            Where: new FieldFilter(FP("tag"), op, new StringValue("x")));
        var result = QueryToConstraints.Convert(query);
        Assert.Empty(result.Constraints);
    }

    [Fact]
    public void CompositeAnd_ExtractsAllFieldFilters()
    {
        var query = new Query("orders",
            Where: new CompositeFilter(CompositeOp.And, [
                new FieldFilter(FP("status"), FilterOperator.Eq, new StringValue("open")),
                new FieldFilter(FP("amount"), FilterOperator.Gt, new IntegerValue(0)),
            ]));
        var result = QueryToConstraints.Convert(query);

        Assert.Equal(2, result.Constraints.Count);
        Assert.Equal(new[] { "status" }, result.Constraints[0].Field);
        Assert.Equal(new[] { "amount" }, result.Constraints[1].Field);
    }

    [Fact]
    public void CompositeOr_IsSkipped_ProducesNoConstraints()
    {
        var query = new Query("col",
            Where: new CompositeFilter(CompositeOp.Or, [
                new FieldFilter(FP("x"), FilterOperator.Eq, new IntegerValue(1)),
                new FieldFilter(FP("x"), FilterOperator.Eq, new IntegerValue(2)),
            ]));
        var result = QueryToConstraints.Convert(query);
        Assert.Empty(result.Constraints);
    }

    [Fact]
    public void CompositeNot_IsSkipped_ProducesNoConstraints()
    {
        var query = new Query("col",
            Where: new CompositeFilter(CompositeOp.Not, [
                new FieldFilter(FP("active"), FilterOperator.Eq, new BooleanValue(false)),
            ]));
        var result = QueryToConstraints.Convert(query);
        Assert.Empty(result.Constraints);
    }

    [Fact]
    public void NestedAndInAnd_ExtractsAllFilters()
    {
        var query = new Query("col",
            Where: new CompositeFilter(CompositeOp.And, [
                new FieldFilter(FP("a"), FilterOperator.Eq, new IntegerValue(1)),
                new CompositeFilter(CompositeOp.And, [
                    new FieldFilter(FP("b"), FilterOperator.Lt, new IntegerValue(5)),
                ]),
            ]));
        var result = QueryToConstraints.Convert(query);
        Assert.Equal(2, result.Constraints.Count);
    }

    [Fact]
    public void MultiSegmentFieldPath_IsPreserved()
    {
        var query = new Query("users",
            Where: new FieldFilter(FieldPath.Parse("address.city"), FilterOperator.Eq, new StringValue("NYC")));
        var result = QueryToConstraints.Convert(query);

        Assert.Single(result.Constraints);
        Assert.Equal(new[] { "address", "city" }, result.Constraints[0].Field);
    }

    [Fact]
    public void AndWithUnsupportedFilter_SkipsUnsupported_KeepsSupported()
    {
        var query = new Query("items",
            Where: new CompositeFilter(CompositeOp.And, [
                new FieldFilter(FP("active"), FilterOperator.Eq, new BooleanValue(true)),
                new FieldFilter(FP("tags"), FilterOperator.ArrayContains, new StringValue("sale")),
            ]));
        var result = QueryToConstraints.Convert(query);

        Assert.Single(result.Constraints);
        Assert.Equal(new[] { "active" }, result.Constraints[0].Field);
    }
}
