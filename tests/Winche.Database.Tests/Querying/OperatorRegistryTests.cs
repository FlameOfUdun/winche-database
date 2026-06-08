// tests/Winche.Database.Tests/Querying/OperatorRegistryTests.cs
using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Sql;
using Winche.Database.Values;

namespace Winche.Database.Tests.Querying;

public class OperatorRegistryTests
{
    private static FieldPath F(string p) => FieldPath.Parse(p);

    private static (string Sql, int ParamCount) Emit(Filter f)
    {
        var bag = new ParameterBag();
        var sql = OperatorRegistry.Emit(f, bag, "d");
        return (sql, bag.ToArray().Length);
    }

    [Fact]
    public void FieldSegments_AreParameterized_NeverInterpolated()
    {
        var evil = "x'; DROP TABLE documents; --";
        var (sql, _) = Emit(new FieldFilter(FieldPath.Parse(evil), FilterOperator.Eq, new IntegerValue(1)));
        Assert.DoesNotContain("DROP TABLE", sql);
    }

    [Fact]
    public void StringValues_AreParameterized()
    {
        var (sql, _) = Emit(new FieldFilter(F("name"), FilterOperator.Eq, new StringValue("'; DELETE FROM x; --")));
        Assert.DoesNotContain("DELETE", sql);
    }

    [Fact]
    public void NumberEq_GuardsRankAndComparesNum()
    {
        var (sql, _) = Emit(new FieldFilter(F("age"), FilterOperator.Eq, new IntegerValue(5)));
        Assert.Contains("winche_rank(", sql);
        Assert.Contains("= 30", sql);
        Assert.Contains("winche_num(", sql);
    }

    [Fact]
    public void StringInequality_UsesCollateC()
    {
        var (sql, _) = Emit(new FieldFilter(F("name"), FilterOperator.Gt, new StringValue("m")));
        Assert.Contains("COLLATE \"C\"", sql);
        Assert.Contains("= 50", sql);
    }

    [Fact]
    public void InequalityWithNaN_IsFalse()
    {
        var (sql, _) = Emit(new FieldFilter(F("x"), FilterOperator.Gt, new DoubleValue(double.NaN)));
        Assert.Equal("FALSE", sql);
    }

    [Fact]
    public void EqNaN_MatchesNaNRank()
    {
        var (sql, _) = Emit(new FieldFilter(F("x"), FilterOperator.Eq, new DoubleValue(double.NaN)));
        Assert.Contains("= 29", sql);
        Assert.DoesNotContain("winche_num", sql);
    }

    [Fact]
    public void Ne_ExcludesMissingNullAndNaN()
    {
        var (sql, _) = Emit(new FieldFilter(F("x"), FilterOperator.Ne, new IntegerValue(1)));
        Assert.Contains("IS NOT NULL", sql);
        Assert.Contains("<> 10", sql);
        Assert.Contains("<> 29", sql);
        Assert.Contains("NOT (", sql);
    }

    [Fact]
    public void In_ExpandsToOrOfEqualities()
    {
        var (sql, count) = Emit(new FieldFilter(F("x"), FilterOperator.In,
            new ArrayValue([new IntegerValue(1), new StringValue("a")])));
        Assert.Contains(" OR ", sql);
        Assert.True(count >= 2);
    }

    [Fact]
    public void ArrayContains_UsesWincheKeyElementEquality()
    {
        var (sql, _) = Emit(new FieldFilter(F("tags"), FilterOperator.ArrayContains, new IntegerValue(1)));
        Assert.Contains("= 90", sql);
        Assert.Contains("jsonb_array_elements(", sql);
        Assert.Contains("winche_key(", sql);
    }

    [Fact]
    public void StartsWith_EscapesLikeMetacharacters()
    {
        var bag = new ParameterBag();
        OperatorRegistry.Emit(new FieldFilter(F("s"), FilterOperator.StartsWith, new StringValue("50%_off")), bag, "d");
        var values = bag.ToArray().Select(p => p.Value).OfType<string>().ToList();
        Assert.Contains(@"50\%\_off%", values);
    }

    [Fact]
    public void Unary_EmitRankChecks()
    {
        Assert.Contains("= 10", Emit(new UnaryFilter(F("x"), UnaryOp.IsNull)).Sql);
        Assert.Contains("= 29", Emit(new UnaryFilter(F("x"), UnaryOp.IsNan)).Sql);
        Assert.Contains("IS NOT NULL", Emit(new UnaryFilter(F("x"), UnaryOp.Exists)).Sql);
    }

    [Fact]
    public void Composite_WrapsInParens()
    {
        var inner = new UnaryFilter(F("x"), UnaryOp.Exists);
        Assert.StartsWith("(", Emit(new CompositeFilter(CompositeOp.Or, [inner, inner])).Sql);
        Assert.StartsWith("NOT (", Emit(new CompositeFilter(CompositeOp.Not, [inner])).Sql);
    }

    [Fact]
    public void FieldCompare_UsesWincheKeyBothSides()
    {
        var (sql, _) = Emit(new FieldCompare(F("a"), FilterOperator.Lt, F("b")));
        Assert.Equal(2, sql.Split("winche_key(").Length - 1 - (sql.Contains("winche_key(winche_key") ? 1 : 0));
        Assert.Contains(" < ", sql);
    }

    [Fact]
    public void NameField_ComparesPathColumnWithCollateC()
    {
        var (sql, _) = Emit(new FieldFilter(F("__name__"), FilterOperator.Gt, new StringValue("users/u1")));
        Assert.Contains("d.path", sql);
        Assert.Contains("COLLATE \"C\"", sql);
        Assert.DoesNotContain("winche_rank", sql);
    }
}
