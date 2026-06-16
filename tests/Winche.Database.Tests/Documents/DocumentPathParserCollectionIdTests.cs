using Winche.Database.Documents;
using Xunit;

namespace Winche.Database.Tests.Documents;

public class DocumentPathParserCollectionIdTests
{
    [Theory]
    [InlineData("users", "users")]
    [InlineData("userData/alice/sessionHistory", "sessionHistory")]
    [InlineData("a/b/c", "c")]
    public void CollectionIdOf_ReturnsLastSegment(string collectionPath, string expected) =>
        Assert.Equal(expected, DocumentPathParser.CollectionIdOf(collectionPath));

    [Fact]
    public void ParsePath_PopulatesCollectionId()
    {
        var info = DocumentPathParser.ParsePath("userData/alice/sessionHistory/s1");
        Assert.Equal("userData/alice/sessionHistory", info.Collection);
        Assert.Equal("s1", info.Id);
        Assert.Equal("sessionHistory", info.CollectionId);
    }

    [Theory]
    [InlineData("sessionHistory", true)]
    [InlineData("user_data-1", true)]
    [InlineData("a/b", false)]
    [InlineData("a*", false)]
    [InlineData("", false)]
    public void IsValidCollectionId_Validates(string id, bool valid) =>
        Assert.Equal(valid, DocumentPathParser.IsValidCollectionId(id, out _));
}
