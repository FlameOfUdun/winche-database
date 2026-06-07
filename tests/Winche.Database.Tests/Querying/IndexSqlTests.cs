using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Sql;

namespace Winche.Database.Tests.Querying;

file sealed class AgeIndex : IndexDefinition
{
    public override string Collection => "users";
    public override IReadOnlyList<IndexField> Fields => [new("age"), new("addr.city", SortDirection.Desc)];
}

file sealed class EvilIndex(string path) : IndexDefinition
{
    private readonly string _path = path;
    public override string Collection => "users";
    public override IReadOnlyList<IndexField> Fields => [new(_path)];
}

// Two index definitions whose old (pre-hash) names would collide: "a.b"+"c" vs "a"+"b-c"
file sealed class CollidingIndexA : IndexDefinition
{
    public override string Collection => "col";
    public override IReadOnlyList<IndexField> Fields => [new("a.b"), new("c")];
}

file sealed class CollidingIndexB : IndexDefinition
{
    public override string Collection => "col";
    public override IReadOnlyList<IndexField> Fields => [new("a"), new("b-c")];
}

file sealed class EmptyFieldsIndex : IndexDefinition
{
    public override string Collection => "col";
    public override IReadOnlyList<IndexField> Fields => [];
}

public class IndexSqlTests
{
    [Fact]
    public void BuildCreate_EmitsExpressionFamilyAndPartialClause()
    {
        var sql = IndexSql.BuildCreate(new AgeIndex(), "public", "documents");
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
        Assert.Throws<ArgumentException>(() => IndexSql.BuildCreate(new EvilIndex(path), "public", "documents"));

    [Fact]
    public void BuildDrop_QuotesName()
    {
        Assert.Equal("DROP INDEX IF EXISTS \"public\".\"idx_x\"", IndexSql.BuildDrop("public", "idx_x"));
        Assert.Throws<ArgumentException>(() => IndexSql.BuildDrop("public", "bad\"name"));
    }

    [Fact]
    public void BuildCreate_DistinctDefinitions_ProduceDifferentNames()
    {
        var sqlA = IndexSql.BuildCreate(new CollidingIndexA(), "public", "documents");
        var sqlB = IndexSql.BuildCreate(new CollidingIndexB(), "public", "documents");
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
        Assert.Throws<ArgumentException>(() => IndexSql.BuildCreate(new EmptyFieldsIndex(), "public", "documents"));
}
