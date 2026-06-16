using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Planning;
using Winche.Database.Querying.Sql;

namespace Winche.Database.Tests.Querying;

public class PatternScopeCompilerTests
{
    [Fact]
    public void Query_WithCollectionIdScope_EmitsCollectionIdPredicate()
    {
        var plan = Normalizer.Normalize(new Query("userData/alice/sessionHistory"));
        var sql = SqlCompiler.Compile(plan, "sessionHistory").Sql;
        Assert.Contains("collection_id = ", sql);
        Assert.DoesNotContain("collection_path ~", sql);
    }

    [Fact]
    public void Query_WithoutCollectionIdScope_NoCollectionIdPredicate()
    {
        var plan = Normalizer.Normalize(new Query("userData/alice/sessionHistory"));
        var sql = SqlCompiler.Compile(plan).Sql;
        Assert.DoesNotContain("collection_id =", sql);
        Assert.DoesNotContain("collection_path ~", sql);
    }

    [Fact]
    public void Query_WithCollectionIdScope_StillFiltersOnCollectionPath()
    {
        var plan = Normalizer.Normalize(new Query("userData/alice/sessionHistory"));
        var sql = SqlCompiler.Compile(plan, "sessionHistory").Sql;
        // Must still filter on the exact collection path (to isolate alice's from bob's)
        Assert.Contains("collection_path =", sql);
        // AND must also filter on collection_id so the partial index is used
        Assert.Contains("collection_id =", sql);
    }
}
