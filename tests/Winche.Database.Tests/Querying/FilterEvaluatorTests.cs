// tests/Winche.Database.Tests/Querying/FilterEvaluatorTests.cs
using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Matching;
using Winche.Database.Values;

namespace Winche.Database.Tests.Querying;

public class FilterEvaluatorTests
{
    private static FieldPath F(string p) => FieldPath.Parse(p);

    private static readonly Dictionary<string, Value> Doc = new()
    {
        ["age"] = new IntegerValue(30),
        ["score"] = new DoubleValue(30.0),
        ["name"] = new StringValue("Ada"),
        ["nul"] = new NullValue(),
        ["nan"] = new DoubleValue(double.NaN),
        ["tags"] = new ArrayValue([new IntegerValue(1), new StringValue("x")]),
        ["addr"] = new MapValue(new Dictionary<string, Value> { ["city"] = new StringValue("Oslo") }),
    };

    private static bool Eval(FilterAst f) => FilterEvaluator.Matches(f, "c/d1", Doc);

    [Fact]
    public void Eq_TypedWithNumericEquivalence()
    {
        Assert.True(Eval(new FieldFilterAst(F("age"), FilterOperator.Eq, new DoubleValue(30.0))));
        Assert.True(Eval(new FieldFilterAst(F("score"), FilterOperator.Eq, new IntegerValue(30))));
        Assert.False(Eval(new FieldFilterAst(F("age"), FilterOperator.Eq, new StringValue("30"))));
        Assert.True(Eval(new FieldFilterAst(F("addr.city"), FilterOperator.Eq, new StringValue("Oslo"))));
    }

    [Fact]
    public void Inequality_SameClassOnly_MissingNeverMatches()
    {
        Assert.True(Eval(new FieldFilterAst(F("age"), FilterOperator.Gt, new IntegerValue(20))));
        Assert.False(Eval(new FieldFilterAst(F("age"), FilterOperator.Gt, new StringValue("a"))));
        Assert.False(Eval(new FieldFilterAst(F("missing"), FilterOperator.Gt, new IntegerValue(0))));
        Assert.False(Eval(new FieldFilterAst(F("nan"), FilterOperator.Lte, new DoubleValue(99))));   // NaN field matches no range
        Assert.False(Eval(new FieldFilterAst(F("age"), FilterOperator.Gt, new DoubleValue(double.NaN)))); // NaN operand matches nothing
    }

    [Fact]
    public void Ne_ExcludesMissingNullNaN_MatchesCrossType()
    {
        Assert.True(Eval(new FieldFilterAst(F("name"), FilterOperator.Ne, new IntegerValue(1))));    // cross-type matches Ne
        Assert.False(Eval(new FieldFilterAst(F("missing"), FilterOperator.Ne, new IntegerValue(1))));
        Assert.False(Eval(new FieldFilterAst(F("nul"), FilterOperator.Ne, new IntegerValue(1))));
        Assert.False(Eval(new FieldFilterAst(F("nan"), FilterOperator.Ne, new IntegerValue(1))));
        Assert.False(Eval(new FieldFilterAst(F("score"), FilterOperator.Ne, new IntegerValue(30)))); // 30.0 == 30
    }

    [Fact]
    public void InNotIn_ArrayOps_StringOps()
    {
        Assert.True(Eval(new FieldFilterAst(F("age"), FilterOperator.In,
            new ArrayValue([new DoubleValue(30.0), new StringValue("z")]))));
        Assert.False(Eval(new FieldFilterAst(F("nul"), FilterOperator.NotIn, new ArrayValue([new IntegerValue(1)]))));
        Assert.True(Eval(new FieldFilterAst(F("tags"), FilterOperator.ArrayContains, new DoubleValue(1.0))));
        Assert.True(Eval(new FieldFilterAst(F("tags"), FilterOperator.ArrayContainsAny,
            new ArrayValue([new StringValue("x"), new StringValue("q")]))));
        Assert.True(Eval(new FieldFilterAst(F("tags"), FilterOperator.ArrayContainsAll,
            new ArrayValue([new IntegerValue(1), new StringValue("x")]))));
        Assert.True(Eval(new FieldFilterAst(F("name"), FilterOperator.StartsWith, new StringValue("ad")))); // case-insensitive
        Assert.True(Eval(new FieldFilterAst(F("name"), FilterOperator.Contains, new StringValue("D"))));
        Assert.True(Eval(new FieldFilterAst(F("name"), FilterOperator.Regex, new StringValue("^A.a$"))));
        Assert.False(Eval(new FieldFilterAst(F("name"), FilterOperator.Regex, new StringValue("^a"))));      // case-SENSITIVE
    }

    [Fact]
    public void Unary_Composite_Compare_Name()
    {
        Assert.True(Eval(new UnaryFilterAst(F("nul"), UnaryOp.IsNull)));
        Assert.True(Eval(new UnaryFilterAst(F("nan"), UnaryOp.IsNan)));
        Assert.True(Eval(new UnaryFilterAst(F("nul"), UnaryOp.Exists)));
        Assert.False(Eval(new UnaryFilterAst(F("missing"), UnaryOp.Exists)));
        Assert.True(Eval(new CompositeFilterAst(CompositeOp.Not,
            [new UnaryFilterAst(F("missing"), UnaryOp.Exists)])));
        Assert.True(Eval(new CompositeFilterAst(CompositeOp.And,
        [
            new FieldFilterAst(F("age"), FilterOperator.Gte, new IntegerValue(30)),
            new CompositeFilterAst(CompositeOp.Or,
            [
                new FieldFilterAst(F("name"), FilterOperator.Eq, new StringValue("Ada")),
                new FieldFilterAst(F("name"), FilterOperator.Eq, new StringValue("Bob")),
            ]),
        ])));
        Assert.True(Eval(new FieldCompareAst(F("age"), FilterOperator.Eq, F("score"))));   // 30 == 30.0
        Assert.False(Eval(new FieldCompareAst(F("age"), FilterOperator.Lt, F("missing")))); // missing never matches
        Assert.True(Eval(new FieldFilterAst(F("__name__"), FilterOperator.Eq, new StringValue("c/d1"))));
        Assert.True(Eval(new FieldFilterAst(F("__name__"), FilterOperator.Gt, new StringValue("c/c"))));
    }
}
