using Winche.Database.Querying.Sql;

namespace Winche.Database.Tests.Querying;

public class CollectionListSqlTests
{
    [Fact]
    public void Root_UsesFirstSegment_NoRangeBounds()
    {
        var result = CollectionListSql.Build(parentDocumentPath: null, after: null, limit: 50);

        Assert.Contains("split_part(document_path, '/', 1)", result.Sql);
        Assert.Contains("WHERE TRUE", result.Sql);                 // no descendant range for root
        Assert.Contains("SELECT DISTINCT split_part(document_path, '/', 1)", result.Sql);
        Assert.Contains("ORDER BY cid COLLATE \"C\"", result.Sql);
        // Params: $1 = after (null), $2 = limit
        Assert.Equal(2, result.Parameters.Length);
        Assert.Equal(DBNull.Value, result.Parameters[0].Value);
        Assert.Equal(50, result.Parameters[1].Value);
    }

    [Fact]
    public void UnderDocument_RangeBoundsAndSubstr_AreParameterized()
    {
        var result = CollectionListSql.Build(parentDocumentPath: "users/u1", after: null, limit: 50);

        // Descendant range on the PK, byte-ordered
        Assert.Contains("document_path >= $1 COLLATE \"C\"", result.Sql);
        Assert.Contains("document_path < $2 COLLATE \"C\"", result.Sql);
        // Child segment extracted relative to the parent prefix length
        Assert.Contains("split_part(substr(document_path, char_length($1) + 1), '/', 1)", result.Sql);
        // Values never interpolated into SQL text
        Assert.DoesNotContain("users/u1", result.Sql);
        // $1 = "users/u1/" (lo prefix), $2 = "users/u10" (exclusive upper bound: '/'+1 == '0')
        Assert.Equal("users/u1/", result.Parameters[0].Value);
        Assert.Equal("users/u10", result.Parameters[1].Value);
        // $3 = after (null), $4 = limit
        Assert.Equal(4, result.Parameters.Length);
        Assert.Equal(DBNull.Value, result.Parameters[2].Value);
        Assert.Equal(50, result.Parameters[3].Value);
    }

    [Fact]
    public void After_IsBoundAsKeysetCursor()
    {
        var result = CollectionListSql.Build(parentDocumentPath: null, after: "orders", limit: 10);

        Assert.Contains("cid > $1::text COLLATE \"C\"", result.Sql);
        Assert.Equal("orders", result.Parameters[0].Value);
    }
}
