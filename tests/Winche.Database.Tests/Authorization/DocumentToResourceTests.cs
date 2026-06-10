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
    public void Convert_ProducesMap_WithDataIdAndName()
    {
        var doc = MakeDocument("users/u1", "u1", "users");
        var result = DocumentToResource.Convert(doc);

        Assert.Equal(RuleValueKind.Map, result.Kind);
        var map = result.AsMap;
        Assert.True(map.ContainsKey("data"));
        Assert.True(map.ContainsKey("id"));
        Assert.True(map.ContainsKey("__name__"));
    }

    [Fact]
    public void Convert_Id_IsLastPathSegment()
    {
        var doc = MakeDocument("users/u42", "u42", "users");
        var result = DocumentToResource.Convert(doc);
        Assert.Equal(RuleValueKind.String, result.AsMap["id"].Kind);
        Assert.Equal("u42", result.AsMap["id"].AsString);
    }

    [Fact]
    public void Convert_Name_IsFullPathAsRulePath()
    {
        var doc = MakeDocument("users/u42", "u42", "users");
        var result = DocumentToResource.Convert(doc);
        var name = result.AsMap["__name__"];
        Assert.Equal(RuleValueKind.Path, name.Kind);
        Assert.Equal("users/u42", name.AsPath);
    }

    [Fact]
    public void Convert_Data_ContainsAllFields()
    {
        var fields = new Dictionary<string, Value>
        {
            ["age"] = new IntegerValue(30),
            ["name"] = new StringValue("Alice"),
        };
        var doc = MakeDocument("users/u1", "u1", "users", fields);
        var result = DocumentToResource.Convert(doc);
        var data = result.AsMap["data"];

        Assert.Equal(RuleValueKind.Map, data.Kind);
        Assert.Equal(30L, data.AsMap["age"].AsInt);
        Assert.Equal("Alice", data.AsMap["name"].AsString);
    }

    [Fact]
    public void Convert_EmptyDocument_DataIsEmptyMap()
    {
        var doc = MakeDocument("col/doc1", "doc1", "col");
        var result = DocumentToResource.Convert(doc);
        var data = result.AsMap["data"];
        Assert.Equal(RuleValueKind.Map, data.Kind);
        Assert.Empty(data.AsMap);
    }

    [Fact]
    public void Convert_NestedFields_AreConvertedRecursively()
    {
        var fields = new Dictionary<string, Value>
        {
            ["address"] = new MapValue(new Dictionary<string, Value>
            {
                ["city"] = new StringValue("London"),
            }),
        };
        var doc = MakeDocument("users/u1", "u1", "users", fields);
        var result = DocumentToResource.Convert(doc);
        var city = result.AsMap["data"].AsMap["address"].AsMap["city"];
        Assert.Equal("London", city.AsString);
    }
}
