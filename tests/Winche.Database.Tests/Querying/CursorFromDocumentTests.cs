using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Values;

namespace Winche.Database.Tests.Querying;

public class CursorFromDocumentTests
{
    private static Document Doc(string path, params (string K, Value V)[] fields) => new()
    {
        Path = path,
        Id = path[(path.LastIndexOf('/') + 1)..],
        Collection = path[..path.LastIndexOf('/')],
        Fields = fields.ToDictionary(x => x.K, x => x.V),
        CreateTime = DateTimeOffset.UnixEpoch,
        UpdateTime = DateTimeOffset.UnixEpoch,
        Version = 1,
    };

    [Fact]
    public void Derives_Values_InOrder_WithName_AsReference()
    {
        var doc = Doc("c/a", ("age", new IntegerValue(30)), ("name", new StringValue("ada")));
        var cursor = Cursor.FromDocument(doc, [new Ordering(FieldPath.Parse("age"))], before: false);

        Assert.False(cursor.Before);
        Assert.Equal(2, cursor.Values.Count);
        Assert.Equal(new IntegerValue(30), cursor.Values[0]);
        Assert.Equal(new ReferenceValue("c/a"), cursor.Values[1]); // appended __name__
    }

    [Fact]
    public void NestedDottedField_Extracted()
    {
        var doc = Doc("c/a", ("address", new MapValue(new Dictionary<string, Value> { ["city"] = new StringValue("paris") })));
        var cursor = Cursor.FromDocument(doc, [new Ordering(FieldPath.Parse("address.city"))], before: true);
        Assert.Equal(new StringValue("paris"), cursor.Values[0]);
        Assert.Equal(new ReferenceValue("c/a"), cursor.Values[1]);
    }

    [Fact]
    public void ExplicitName_InOrderBy_NotDoubled()
    {
        var doc = Doc("c/a", ("age", new IntegerValue(30)));
        var cursor = Cursor.FromDocument(doc,
            [new Ordering(FieldPath.Parse("age")), new Ordering(FieldPath.Parse("__name__"))], before: false);
        Assert.Equal(2, cursor.Values.Count);
        Assert.Equal(new IntegerValue(30), cursor.Values[0]);
        Assert.Equal(new ReferenceValue("c/a"), cursor.Values[1]);
    }

    [Fact]
    public void MissingField_Throws_NamingTheField()
    {
        var doc = Doc("c/a", ("name", new StringValue("ada")));
        var ex = Assert.Throws<ArgumentException>(
            () => Cursor.FromDocument(doc, [new Ordering(FieldPath.Parse("age"))], before: false));
        Assert.Contains("age", ex.Message);
    }

    [Fact]
    public void NonMapMidPath_Throws()
    {
        var doc = Doc("c/a", ("address", new StringValue("paris")));
        Assert.Throws<ArgumentException>(
            () => Cursor.FromDocument(doc, [new Ordering(FieldPath.Parse("address.city"))], before: false));
    }

    [Fact]
    public void EmptyOrderBy_YieldsNameOnlyCursor()
    {
        var doc = Doc("c/a", ("name", new StringValue("ada")));
        var cursor = Cursor.FromDocument(doc, [], before: true);
        Assert.True(cursor.Before);
        Assert.Equal(new ReferenceValue("c/a"), Assert.Single(cursor.Values));
    }
}
