// tests/Winche.Database.Tests/Querying/SchemaResolverTests.cs
using Winche.Database.Documents;
using Winche.Database.Querying.Sql;

namespace Winche.Database.Tests.Querying;

public class SchemaResolverTests
{
    private static FieldPath F(string p) => FieldPath.Parse(p);

    [Fact]
    public void PlainDocument_DataField_IsParameterizedAccessor()
    {
        var bag = new ParameterBag();
        var r = new SchemaResolver(DocumentSchema.Plain, "d").Resolve(F("a.b"), bag);
        var t = Assert.IsType<TaggedRef>(r);
        Assert.StartsWith("d.data->", t.Sql);
        Assert.Contains("'mapValue'", t.Sql);
        Assert.Equal(2, bag.ToArray().Length);          // both segments parameterized
    }

    [Fact]
    public void PlainDocument_Name_IsPathRef()
    {
        var bag = new ParameterBag();
        var r = new SchemaResolver(DocumentSchema.Plain, "d").Resolve(F("__name__"), bag);
        Assert.Equal("d.document_path", Assert.IsType<PathRef>(r).Sql);
    }

    [Fact]
    public void DocumentWithExtra_RootSegmentMatchesExtraColumn()
    {
        var schema = new DocumentSchema(new Dictionary<string, ColumnShape> { ["user"] = ColumnShape.TaggedValue });
        var bag = new ParameterBag();
        var r = new SchemaResolver(schema, "s1").Resolve(F("user.age"), bag);
        var t = Assert.IsType<TaggedRef>(r);
        Assert.StartsWith("s1.\"user\"->'mapValue'->'fields'->", t.Sql);
        Assert.Single(bag.ToArray());                   // only the nested segment parameterized
    }

}
