using Winche.Database.Querying.Sql;

namespace Winche.Database.Tests.Querying;

public class DocumentSqlTests
{
    [Fact]
    public void Get_ParameterizesPath()
    {
        var result = DocumentSql.Get("users/u1");
        Assert.Contains("WHERE path = $1", result.Sql);
        Assert.Single(result.Parameters);
        Assert.Equal("users/u1", result.Parameters[0].Value);
    }

    [Fact]
    public void Upsert_ParameterizesEverything_NoValueInterpolation()
    {
        var result = DocumentSql.Upsert("users/u1", "u1", "users", """{"a":{"integerValue":"1"}}""");
        Assert.Equal(4, result.Parameters.Length);
        Assert.DoesNotContain("u1", result.Sql);          // values never appear in SQL text
        Assert.DoesNotContain("integerValue", result.Sql);
        Assert.Contains("ON CONFLICT (path)", result.Sql);
        Assert.Contains("version + 1", result.Sql);
        Assert.Contains("RETURNING", result.Sql);
    }

    [Fact]
    public void DeleteSubtree_EscapesLikeMetacharacters()
    {
        var result = DocumentSql.DeleteSubtree("users/100%_x");
        Assert.Equal(2, result.Parameters.Length);
        Assert.Equal("users/100%_x", result.Parameters[0].Value);          // exact match: raw
        Assert.Equal(@"users/100\%\_x/%", result.Parameters[1].Value);     // prefix match: escaped
        Assert.Contains("RETURNING path", result.Sql);
    }

    [Fact]
    public void UpdateData_ParameterizesPathAndData()
    {
        var result = DocumentSql.UpdateData("users/u1", "{}");
        Assert.Equal(2, result.Parameters.Length);
        Assert.Contains("version = version + 1", result.Sql);
        Assert.Contains("RETURNING", result.Sql);
    }

    [Fact]
    public void Get_ForUpdate_AppendsLockClause()
    {
        Assert.EndsWith("FOR UPDATE", DocumentSql.Get("users/u1", forUpdate: true).Sql);
        Assert.DoesNotContain("FOR UPDATE", DocumentSql.Get("users/u1").Sql);
    }
}
