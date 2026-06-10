using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Planning;
using Winche.Database.Querying.Sql;

namespace Winche.Database.Tests.Querying;

public class PatternScopeCompilerTests
{
    private static readonly string[] Rx = ["^userData/[^/]+/sessionHistory$"];

    [Fact]
    public void Query_WithScopeRegex_EmitsRegexPredicate()
    {
        var plan = Normalizer.Normalize(new Query("userData/alice/sessionHistory"));
        var sql = SqlCompiler.Compile(plan, Rx).Sql;
        Assert.Contains("collection ~ '^userData/[^/]+/sessionHistory$'", sql);
    }

    [Fact]
    public void Query_WithoutScopeRegex_NoRegexPredicate()
    {
        var plan = Normalizer.Normalize(new Query("userData/alice/sessionHistory"));
        Assert.DoesNotContain("collection ~", SqlCompiler.Compile(plan).Sql);
    }
}
