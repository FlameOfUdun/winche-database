// tests/Winche.Database.Tests/Querying/IndexPredicateSqlTests.cs
using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Sql;
using Winche.Database.Values;

namespace Winche.Database.Tests.Querying;

public class IndexPredicateSqlTests
{
    private static FieldPath F(string p) => FieldPath.Parse(p);
    private static string Emit(Filter f) => IndexPredicateSql.Emit(f);

    [Fact]
    public void StringEq_QuoteDoubled_CollateC()
    {
        var sql = Emit(new FieldFilter(F("name"), FilterOperator.Eq, new StringValue("O'Brien")));
        Assert.Contains("'O''Brien'", sql);
        Assert.Contains("COLLATE \"C\"", sql);
        Assert.Contains("= 50", sql);
    }

    [Fact]
    public void NumericRange_BoolEq_TimestampEq()
    {
        Assert.Contains("> 21", Emit(new FieldFilter(F("age"), FilterOperator.Gt, new IntegerValue(21))));
        Assert.Contains("= 1", Emit(new FieldFilter(F("on"), FilterOperator.Eq, new BooleanValue(true))));
        var ts = Emit(new FieldFilter(F("at"), FilterOperator.Lte,
            new TimestampValue(DateTimeOffset.UnixEpoch.AddSeconds(1))));
        Assert.Contains("1000000", ts);                                  // epoch µs literal
        Assert.Contains("= 40", ts);
    }

    [Fact]
    public void Unaries_And()
    {
        Assert.Contains("IS NOT NULL", Emit(new UnaryFilter(F("x"), UnaryOp.Exists)));
        Assert.Contains("= 10", Emit(new UnaryFilter(F("x"), UnaryOp.IsNull)));
        var and = Emit(new CompositeFilter(CompositeOp.And,
        [
            new FieldFilter(F("a"), FilterOperator.Eq, new IntegerValue(1)),
            new UnaryFilter(F("b"), UnaryOp.Exists),
        ]));
        Assert.Contains(" AND ", and);
    }

    [Theory]
    [InlineData("control char")]
    [InlineData("or")]
    [InlineData("operandKind")]
    public void Rejections(string @case)
    {
        Filter bad = @case switch
        {
            "control char" => new FieldFilter(F("s"), FilterOperator.Eq, new StringValue("a\nb")),
            "or" => new CompositeFilter(CompositeOp.Or,
                [new UnaryFilter(F("a"), UnaryOp.Exists), new UnaryFilter(F("b"), UnaryOp.Exists)]),
            _ => new FieldFilter(F("a"), FilterOperator.Eq, new ArrayValue([new IntegerValue(1)])),
        };
        Assert.Throws<ArgumentException>(() => Emit(bad));
    }

    [Fact]
    public void MoreRejections()
    {
        Assert.Throws<ArgumentException>(() => Emit(new FieldFilter(F("d"), FilterOperator.Eq, new DoubleValue(double.NaN))));
        Assert.Throws<ArgumentException>(() => Emit(new FieldFilter(F("x"), FilterOperator.In, new ArrayValue([new IntegerValue(1)]))));
        Assert.Throws<ArgumentException>(() => Emit(new FieldFilter(F("__name__"), FilterOperator.Eq, new StringValue("c/a"))));
        Assert.Throws<ArgumentException>(() => Emit(new UnaryFilter(F("x"), UnaryOp.IsNan)));
        Assert.Throws<ArgumentException>(() => Emit(new FieldCompare(F("a"), FilterOperator.Eq, F("b"))));
        Assert.Throws<ArgumentException>(() => Emit(new FieldFilter(F("bad seg!"), FilterOperator.Eq, new IntegerValue(1))));
    }
}
