using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Matching;
using Winche.Database.Values;

namespace Winche.Database.Tests.Querying;

public class ChangeMatcherTests
{
    private static FieldPath F(string p) => FieldPath.Parse(p);
    private static readonly IReadOnlySet<string> Empty = new HashSet<string>();
    private static readonly IReadOnlySet<string> HasD1 = new HashSet<string> { "d1" };

    [Fact]
    public void Removed_AffectsOnlyIfInSnapshot()
    {
        var q = new QueryAst("c");
        Assert.True(ChangeMatcher.CouldAffect(q, HasD1, "d1", true, "c/d1", null));
        Assert.False(ChangeMatcher.CouldAffect(q, Empty, "d1", true, "c/d1", null));
    }

    [Fact]
    public void NullFields_IsConservative() =>
        Assert.True(ChangeMatcher.CouldAffect(new QueryAst("c"), Empty, "d1", false, "c/d1", null));

    [Fact]
    public void NonMatchingAdd_OutsideSnapshot_DoesNotAffect()
    {
        var q = new QueryAst("c", Where: new FieldFilterAst(F("age"), FilterOperator.Gte, new IntegerValue(18)));
        Assert.False(ChangeMatcher.CouldAffect(q, Empty, "d1", false, "c/d1",
            new Dictionary<string, Value> { ["age"] = new IntegerValue(5) }));
    }

    [Fact]
    public void DotNetInvalidRegex_IsConservative()
    {
        var q = new QueryAst("c", Where: new FieldFilterAst(F("s"), FilterOperator.Regex, new StringValue("[[:alpha:")));
        Assert.True(ChangeMatcher.CouldAffect(q, Empty, "d1", false, "c/d1",
            new Dictionary<string, Value> { ["s"] = new StringValue("x") }));
    }

    [Fact]
    public void ImplicitOrderByExists_Applies()
    {
        var q = new QueryAst("c", OrderBy: [new OrderAst(F("age"))]);
        Assert.False(ChangeMatcher.CouldAffect(q, Empty, "d1", false, "c/d1",
            new Dictionary<string, Value> { ["other"] = new IntegerValue(1) }));
    }
}
