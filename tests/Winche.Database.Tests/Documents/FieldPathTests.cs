using Winche.Database.Documents;

namespace Winche.Database.Tests.Documents;

public class FieldPathTests
{
    [Fact]
    public void Parse_SingleSegment()
    {
        var p = FieldPath.Parse("age");
        Assert.Equal(["age"], p.Segments);
        Assert.Equal("age", p.ToString());
    }

    [Fact]
    public void Parse_NestedSegments()
    {
        var p = FieldPath.Parse("address.city.name");
        Assert.Equal(["address", "city", "name"], p.Segments);
        Assert.Equal("address.city.name", p.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("a..b")]
    [InlineData(".a")]
    [InlineData("a.")]
    public void Parse_RejectsEmptySegments(string input)
    {
        Assert.Throws<ArgumentException>(() => FieldPath.Parse(input));
    }

    [Fact]
    public void Equality_IsStructural()
    {
        Assert.Equal(FieldPath.Parse("a.b"), FieldPath.Parse("a.b"));
        Assert.NotEqual(FieldPath.Parse("a.b"), FieldPath.Parse("a.c"));
    }
}
