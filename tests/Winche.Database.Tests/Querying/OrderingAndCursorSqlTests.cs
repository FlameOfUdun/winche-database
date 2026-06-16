// tests/Winche.Database.Tests/Querying/OrderingAndCursorSqlTests.cs
using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Planning;
using Winche.Database.Querying.Sql;
using Winche.Database.Values;

namespace Winche.Database.Tests.Querying;

public class OrderingAndCursorSqlTests
{
    private static FieldPath F(string p) => FieldPath.Parse(p);

    [Fact]
    public void Ordering_EmitsSixExpressionFamilyPerField()
    {
        var bag = new ParameterBag();
        var sql = OrderingSql.Build([new SortKey(F("age"), SortDirection.Asc)], bag, "d");
        foreach (var fn in new[] { "winche_rank(", "winche_num(", "winche_num2(", "winche_text(", "winche_bytes(", "winche_key(" })
            Assert.Contains(fn, sql);
        Assert.Contains("COLLATE \"C\"", sql);
        Assert.DoesNotContain("DESC", sql);
    }

    [Fact]
    public void Ordering_DescAppliesToAllExpressions()
    {
        var bag = new ParameterBag();
        var sql = OrderingSql.Build([new SortKey(F("age"), SortDirection.Desc)], bag, "d");
        Assert.Equal(6, sql.Split(" DESC").Length - 1);
    }

    [Fact]
    public void Ordering_NameKey_IsPathColumn()
    {
        var bag = new ParameterBag();
        var sql = OrderingSql.Build([new SortKey(F("__name__"), SortDirection.Asc)], bag, "d");
        Assert.Equal("d.document_path COLLATE \"C\" ASC", sql);
    }

    [Fact]
    public void Cursor_SingleKeyExclusiveLower_IsStrictCrossTypeComparison()
    {
        var bag = new ParameterBag();
        var keys = new[] { new SortKey(F("age"), SortDirection.Asc), new SortKey(F("__name__"), SortDirection.Asc) };
        var sql = CursorSql.Build(
            new CursorRangeNode(new SortBoundary([new IntegerValue(30)], Inclusive: false), null), keys, bag, "d");
        Assert.NotNull(sql);
        Assert.Contains("winche_rank(", sql);
        Assert.Contains("> 30", sql);          // rank-strict OR same-class strict
        Assert.DoesNotContain(">=", sql);      // exclusive bound, single level → no inclusive eq
    }

    [Fact]
    public void Cursor_InclusiveBoundary_AddsFullPrefixEquality()
    {
        var bag = new ParameterBag();
        var keys = new[] { new SortKey(F("age"), SortDirection.Asc), new SortKey(F("__name__"), SortDirection.Asc) };
        var sql = CursorSql.Build(
            new CursorRangeNode(new SortBoundary([new IntegerValue(30)], Inclusive: true), null), keys, bag, "d");
        Assert.Contains(" OR ", sql);          // strict-level OR full-equality
        Assert.Contains("winche_num(", sql);
    }

    [Fact]
    public void Cursor_DescendingKey_InvertsComparison()
    {
        var bag = new ParameterBag();
        var keys = new[] { new SortKey(F("age"), SortDirection.Desc), new SortKey(F("__name__"), SortDirection.Desc) };
        var sql = CursorSql.Build(
            new CursorRangeNode(new SortBoundary([new IntegerValue(30)], Inclusive: false), null), keys, bag, "d");
        Assert.Contains("< 30", sql);          // after the boundary in a DESC sort = smaller
    }

    [Fact]
    public void Cursor_TwoValues_ExpandsTuple()
    {
        var bag = new ParameterBag();
        var keys = new[] { new SortKey(F("age"), SortDirection.Asc), new SortKey(F("__name__"), SortDirection.Asc) };
        var sql = CursorSql.Build(
            new CursorRangeNode(new SortBoundary([new IntegerValue(30), new StringValue("c/x")], Inclusive: false), null),
            keys, bag, "d");
        Assert.Contains("d.document_path", sql);        // second key is __name__
        Assert.Contains(" OR ", sql);          // level expansion
    }

    [Fact]
    public void Cursor_LowerAndUpper_AreAnded()
    {
        var bag = new ParameterBag();
        var keys = new[] { new SortKey(F("age"), SortDirection.Asc), new SortKey(F("__name__"), SortDirection.Asc) };
        var sql = CursorSql.Build(
            new CursorRangeNode(
                new SortBoundary([new IntegerValue(18)], Inclusive: true),
                new SortBoundary([new IntegerValue(65)], Inclusive: false)),
            keys, bag, "d");
        Assert.Contains(") AND (", sql);
    }
}
