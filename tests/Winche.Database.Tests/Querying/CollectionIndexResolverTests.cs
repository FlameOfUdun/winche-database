using Winche.Database.Documents;
using Winche.Database.Querying;
using Xunit;

namespace Winche.Database.Tests.Querying;

public class CollectionIndexResolverTests
{
    private static readonly IndexDefinition Idx =
        new("sessionHistory", [new IndexField("startedAt")]);

    [Fact]
    public void ScopeFor_DeclaredCollectionId_ReturnsId()
    {
        var r = new CollectionIndexResolver([Idx]);
        Assert.Equal("sessionHistory", r.ScopeFor("userData/alice/sessionHistory"));
    }

    [Fact]
    public void ScopeFor_UndeclaredCollectionId_ReturnsNull()
    {
        var r = new CollectionIndexResolver([Idx]);
        Assert.Null(r.ScopeFor("users"));
    }

    [Fact]
    public void ScopeFor_DifferentParent_SameCollectionId_ReturnsId()
    {
        var r = new CollectionIndexResolver([Idx]);
        // Both alice and bob's sessionHistory share the same collection ID
        Assert.Equal("sessionHistory", r.ScopeFor("userData/bob/sessionHistory"));
    }

    [Fact]
    public void ScopeFor_TopLevelDeclared_ReturnsId()
    {
        var r = new CollectionIndexResolver([new IndexDefinition("users", [new IndexField("name")])]);
        Assert.Equal("users", r.ScopeFor("users"));
    }
}
