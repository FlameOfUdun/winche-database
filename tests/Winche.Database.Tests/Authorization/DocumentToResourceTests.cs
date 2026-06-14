using Winche.Database.Authorization;
using Winche.Database.Documents;
using Winche.Database.Values;
using Winche.Rules;

namespace Winche.Database.Tests.Authorization;

public class DocumentToResourceTests
{
    private static Document MakeDocument(string path, string id, string collection,
        IReadOnlyDictionary<string, Value>? fields = null) => new()
    {
        Path = path,
        Id = id,
        Collection = collection,
        Fields = fields ?? new Dictionary<string, Value>(),
        CreateTime = DateTimeOffset.UnixEpoch,
        UpdateTime = DateTimeOffset.UnixEpoch,
        Version = 1,
    };

    [Fact]
    public void Convert_ExposesFieldsFlat_AndReservedColumns()
    {
        var fields = new Dictionary<string, Value>
        {
            ["age"] = new IntegerValue(30),
            ["name"] = new StringValue("Alice"),
        };
        var doc = MakeDocument("users/u1", "u1", "users", fields);
        var map = DocumentToResource.Convert(doc).AsMap;

        // No "data" wrapper — fields live at the top level.
        Assert.False(map.ContainsKey("data"));
        Assert.Equal(30L, map["age"].AsInt);
        Assert.Equal("Alice", map["name"].AsString);

        // Reserved storage columns are siblings.
        Assert.Equal("u1", map["id"].AsString);
        Assert.Equal("users/u1", map["path"].AsPath);
        Assert.Equal("users", map["collection"].AsString);
        Assert.Equal(RuleValueKind.Timestamp, map["createdAt"].Kind);
        Assert.Equal(RuleValueKind.Timestamp, map["updatedAt"].Kind);
        Assert.Equal(1L, map["version"].AsInt);

        // "__name__" is gone.
        Assert.False(map.ContainsKey("__name__"));
    }

    [Fact]
    public void Convert_Path_IsFullPathAsRulePath()
    {
        var map = DocumentToResource.Convert(MakeDocument("users/u42", "u42", "users")).AsMap;
        Assert.Equal(RuleValueKind.Path, map["path"].Kind);
        Assert.Equal("users/u42", map["path"].AsPath);
    }

    [Fact]
    public void Convert_EmptyDocument_HasOnlyReservedColumns()
    {
        var map = DocumentToResource.Convert(MakeDocument("col/doc1", "doc1", "col")).AsMap;
        Assert.Equal(
            new[] { "collection", "createdAt", "id", "path", "updatedAt", "version" },
            map.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void Convert_NestedFields_AreConvertedRecursively()
    {
        var fields = new Dictionary<string, Value>
        {
            ["address"] = new MapValue(new Dictionary<string, Value> { ["city"] = new StringValue("London") }),
        };
        var map = DocumentToResource.Convert(MakeDocument("users/u1", "u1", "users", fields)).AsMap;
        Assert.Equal("London", map["address"].AsMap["city"].AsString);
    }

    [Fact]
    public void Convert_ReservedColumn_WinsOverSameNamedField()
    {
        var fields = new Dictionary<string, Value> { ["id"] = new StringValue("from-field") };
        var map = DocumentToResource.Convert(MakeDocument("users/u1", "u1", "users", fields)).AsMap;
        Assert.Equal("u1", map["id"].AsString);   // the document id column wins
    }
}
