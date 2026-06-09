// tests/Winche.Database.Tests/Querying/CountSqlTests.cs
using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Planning;
using Winche.Database.Querying.Sql;
using Winche.Database.Values;

namespace Winche.Database.Tests.Querying;

public class CountSqlTests
{
    private static string Compile(Query query) =>
        CountSql.Compile(Normalizer.Normalize(query), query.Limit).Sql;

    [Fact]
    public void NoLimit_PlainCountOverCollection_NoSubquery_NoOrderBy()
    {
        var sql = Compile(new Query("c"));

        Assert.Contains("SELECT COUNT(*)", sql);
        Assert.Contains("d.collection =", sql);
        Assert.DoesNotContain("SELECT 1 FROM", sql);   // no LIMIT subquery wrap
        Assert.DoesNotContain("ORDER BY", sql);         // count never orders
        Assert.DoesNotContain(" AND ", sql);            // no filter/scope/cursor appended
    }

    [Fact]
    public void ExplicitLimit_WrapsInLimitedSubquery()
    {
        var sql = Compile(new Query("c", Limit: 5));

        Assert.Contains("SELECT COUNT(*) FROM (SELECT 1 FROM", sql);
        Assert.Contains("LIMIT", sql);
        Assert.DoesNotContain("ORDER BY", sql);
    }

    [Fact]
    public void Where_AppendsFilterPredicate()
    {
        var sql = Compile(new Query("c",
            Where: new FieldFilter(FieldPath.Parse("f"), FilterOperator.Gte, new IntegerValue(2))));

        Assert.Contains("SELECT COUNT(*)", sql);
        Assert.Contains(" AND (", sql);                 // the user filter folded into WHERE
    }

    [Fact]
    public void DefaultPageSize_NotApplied_WhenLimitAbsent()
    {
        // Query with no limit must count the full match — the Normalizer's default page size (100)
        // must NOT leak into the count as a LIMIT.
        var sql = Compile(new Query("c"));
        Assert.DoesNotContain("LIMIT", sql);
    }
}
