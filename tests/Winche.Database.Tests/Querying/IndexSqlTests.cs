using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Sql;

namespace Winche.Database.Tests.Querying;

public class IndexSqlTests
{
    private static readonly IndexDefinition AgeIndex =
        new("users", [new("age"), new("addr.city", SortDirection.Desc)]);

    // Two index definitions whose old (pre-hash) names would collide: "a.b"+"c" vs "a"+"b-c"
    private static readonly IndexDefinition CollidingIndexA =
        new("col", [new("a.b"), new("c")]);

    private static readonly IndexDefinition CollidingIndexB =
        new("col", [new("a"), new("b-c")]);

    private static readonly IndexDefinition EmptyFieldsIndex =
        new("col", []);

    private static readonly IndexDefinition SessionHistoryPatternIndex =
        new("userData/*/sessionHistory", [new("startedAt", SortDirection.Desc)]);

    private static readonly IndexDefinition BadCharsetIndex =
        new("user.Data/*/sessionHistory", [new("name")]); // '.' not allowed in literal segment

    [Fact]
    public void BuildCreate_EmitsExpressionFamilyAndPartialClause()
    {
        var sql = IndexSql.BuildCreate(AgeIndex);
        Assert.Contains("CREATE INDEX IF NOT EXISTS", sql);
        Assert.Contains("winche_rank(", sql);
        Assert.Contains("winche_key(", sql);
        Assert.Contains("data->'age'", sql);
        Assert.Contains("->'mapValue'->'fields'->'city'", sql);
        Assert.Contains("DESC", sql);
        Assert.Contains("WHERE collection = 'users'", sql);
        Assert.Contains("COLLATE \"C\"", sql);
    }

    [Theory]
    [InlineData("a'; DROP TABLE x; --")]
    [InlineData("a b")]
    [InlineData("")]
    public void BuildCreate_RejectsInvalidSegments(string path) =>
        Assert.Throws<ArgumentException>(() => IndexSql.BuildCreate(new IndexDefinition("users", [new(path)])));

    [Fact]
    public void BuildDrop_QuotesName()
    {
        Assert.Equal("DROP INDEX IF EXISTS \"idx_x\"", IndexSql.BuildDrop("idx_x"));
        Assert.Throws<ArgumentException>(() => IndexSql.BuildDrop("bad\"name"));
    }

    [Fact]
    public void BuildCreate_DistinctDefinitions_ProduceDifferentNames()
    {
        var sqlA = IndexSql.BuildCreate(CollidingIndexA);
        var sqlB = IndexSql.BuildCreate(CollidingIndexB);
        // Extract names from the SQL (between the first pair of quotes after IF NOT EXISTS)
        static string ExtractName(string sql)
        {
            var start = sql.IndexOf("NOT EXISTS \"", StringComparison.Ordinal) + "NOT EXISTS \"".Length;
            var end = sql.IndexOf('"', start);
            return sql[start..end];
        }
        Assert.NotEqual(ExtractName(sqlA), ExtractName(sqlB));
    }

    [Fact]
    public void BuildCreate_EmptyFields_Throws() =>
        Assert.Throws<ArgumentException>(() => IndexSql.BuildCreate(EmptyFieldsIndex));

    [Fact]
    public void BuildCreate_Pattern_EmitsCollectionLeadingKeyAndRegexPredicate()
    {
        var sql = IndexSql.BuildCreate(SessionHistoryPatternIndex);
        var open = sql.IndexOf('(', sql.IndexOf(" ON ", StringComparison.Ordinal));
        Assert.StartsWith("collection,", sql[(open + 1)..].TrimStart());
        Assert.Contains("winche_rank(data->'startedAt')", sql);
        Assert.Contains(@"WHERE collection ~ '^userData/[^/]+/sessionHistory$'", sql);
    }

    [Fact]
    public void BuildCreate_Exact_StillUsesCollectionEquality()
    {
        var sql = IndexSql.BuildCreate(AgeIndex); // Path => "users"
        Assert.Contains("WHERE collection = 'users'", sql);
        Assert.DoesNotContain("collection ~", sql);
    }

    [Fact]
    public void BuildCreate_InvalidPath_ThrowsInvalidPathPatternException()
    {
        var ex = Assert.Throws<InvalidPathPatternException>(() => IndexSql.BuildCreate(BadCharsetIndex));
        Assert.Equal("user.Data/*/sessionHistory", ex.Path);
        Assert.False(string.IsNullOrEmpty(ex.Reason));
    }
}
