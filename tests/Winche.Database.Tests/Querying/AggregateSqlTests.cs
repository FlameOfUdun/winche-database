// tests/Winche.Database.Tests/Querying/AggregateSqlTests.cs
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Sql;

namespace Winche.Database.Tests.Querying;

public class AggregateSqlTests
{
    private const string T = "x.\"v\"";   // a tagged expression stand-in

    [Fact]
    public void Count_NoField_EmitsTaggedInteger()
    {
        var sql = AggregateSql.Emit(AggFunction.Count, null, windowed: false);
        Assert.Contains("COUNT(*)", sql);
        Assert.Contains("'integerValue'", sql);
        Assert.Contains("::text", sql);
    }

    [Fact]
    public void Count_WithField_CountsNonMissing()
    {
        var sql = AggregateSql.Emit(AggFunction.Count, T, windowed: false);
        Assert.Contains($"COUNT({T})", sql);
    }

    [Fact]
    public void Sum_EmitsTaggedDouble_ZeroWhenEmpty()
    {
        var sql = AggregateSql.Emit(AggFunction.Sum, T, windowed: false);
        Assert.Contains($"SUM(winche_num({T}))", sql);
        Assert.Contains("COALESCE", sql);
        Assert.Contains("'doubleValue'", sql);
    }

    [Fact]
    public void Avg_NullValueWhenEmpty()
    {
        var sql = AggregateSql.Emit(AggFunction.Avg, T, windowed: false);
        Assert.Contains($"AVG(winche_num({T}))", sql);
        Assert.Contains("nullValue", sql);
    }

    [Fact]
    public void MinMax_PickTaggedValueByWincheKey()
    {
        var min = AggregateSql.Emit(AggFunction.Min, T, windowed: false);
        Assert.Contains($"ORDER BY winche_key({T})", min);
        Assert.Contains("FILTER", min);
        var max = AggregateSql.Emit(AggFunction.Max, T, windowed: false);
        Assert.Contains($"ORDER BY winche_key({T}) DESC", max);
    }

    [Fact]
    public void Push_EmitsTaggedArray_EmptyArrayWhenNone()
    {
        var sql = AggregateSql.Emit(AggFunction.Push, T, windowed: false);
        Assert.Contains("'arrayValue'", sql);
        Assert.Contains($"jsonb_agg({T})", sql);
        Assert.Contains("'[]'::jsonb", sql);
    }

    [Fact]
    public void AddToSet_Distinct()
    {
        Assert.Contains($"jsonb_agg(DISTINCT {T})", AggregateSql.Emit(AggFunction.AddToSet, T, windowed: false));
    }

    [Fact]
    public void FirstLast_Positional()
    {
        Assert.Contains("[1]", AggregateSql.Emit(AggFunction.First, T, windowed: false));
        Assert.Contains("cardinality", AggregateSql.Emit(AggFunction.Last, T, windowed: false));
    }

    [Fact]
    public void Windowed_AppendsOverClause()
    {
        var sql = AggregateSql.Emit(AggFunction.Sum, T, windowed: true);
        Assert.Contains("OVER ()", sql);
        var c = AggregateSql.Emit(AggFunction.Count, null, windowed: true);
        Assert.Contains("OVER ()", c);
    }

    [Fact]
    public void Windowed_RejectsUnsupportedFns()
    {
        Assert.Throws<NotSupportedException>(() => AggregateSql.Emit(AggFunction.Min, T, windowed: true));
        Assert.Throws<NotSupportedException>(() => AggregateSql.Emit(AggFunction.Push, T, windowed: true));
    }
}
