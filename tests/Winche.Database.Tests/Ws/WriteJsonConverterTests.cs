using System.Text.Json;
using Winche.Database.Documents;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.Tests.Ws;

/// <summary>
/// Unit tests for <see cref="WriteJsonConverter"/> — the STJ converter that replaces
/// the hand-rolled WriteWireParser. Wire format is unchanged; error type changed from
/// RuntimeException to JsonException (both surface as INVALID_ARGUMENT on the wire).
/// </summary>
public class WriteJsonConverterTests
{
    private static IReadOnlyList<Write> Parse(string json) =>
        JsonSerializer.Deserialize<IReadOnlyList<Write>>(json)
        ?? throw new InvalidOperationException("Deserialize returned null");

    private static JsonException Throws(string json)
    {
        return Assert.Throws<JsonException>(() => Parse(json));
    }

    [Fact]
    public void Set_FullShape()
    {
        var writes = Parse("""
            [{"set":{"path":"c/a","fields":{"x":{"integerValue":"1"}},"merge":true,
              "transforms":[{"field":"n","kind":"increment","operand":{"integerValue":"2"}},
                            {"field":"at","kind":"serverTimestamp"}],
              "precondition":{"exists":true}}}]
            """);
        var set = Assert.IsType<SetWrite>(Assert.Single(writes));
        Assert.Equal("c/a", set.Path);
        Assert.True(set.Merge);
        Assert.Equal(new IntegerValue(1), set.Fields["x"]);
        Assert.Equal(2, set.Transforms!.Count);
        Assert.Equal(TransformKind.Increment, set.Transforms[0].Kind);
        Assert.Equal(FieldPath.Parse("at"), set.Transforms[1].Field);
        Assert.True(set.Precondition!.Exists);
    }

    [Fact]
    public void Update_DottedPaths_AndDeleteFieldSentinel()
    {
        var writes = Parse("""
            [{"update":{"path":"c/a","fields":{"a.b":{"integerValue":"1"},"gone":{"deleteField":true}},
              "precondition":{"updateTime":"2026-06-07T00:00:00Z"}}}]
            """);
        var update = Assert.IsType<UpdateWrite>(Assert.Single(writes));
        Assert.Equal(new IntegerValue(1), update.Fields[FieldPath.Parse("a.b")]);
        Assert.Same(DeleteFieldValue.Instance, update.Fields[FieldPath.Parse("gone")]);
        Assert.Equal(new DateTimeOffset(2026, 6, 7, 0, 0, 0, TimeSpan.Zero), update.Precondition!.UpdateTime);
    }

    [Fact]
    public void Delete_WithCascade()
    {
        var del = Assert.IsType<DeleteWrite>(Assert.Single(Parse("""[{"delete":{"path":"c","cascade":true}}]""")));
        Assert.True(del.Cascade);
    }

    [Fact]
    public void MultiShape_ZeroShape_UnknownKind_BadOperand_Throw()
    {
        Throws("""[{"set":{"path":"c/a","fields":{}},"delete":{"path":"c/a"}}]""");
        Throws("""[{}]""");
        Throws("""[{"bogus":{}}]""");
        Throws("""[{"set":{"path":"c/a","fields":{},"transforms":[{"field":"n","kind":"bogus"}]}}]""");
        Throws("""[{"set":{"path":"c/a","fields":{"x":{"bogusValue":1}}}}]""");
        Throws("""["notAnObject"]""");
    }

    [Fact]
    public void DeleteFieldSentinel_InSet_PassesThroughToRuntimeValidation()
    {
        // the converter accepts it; WriteValidator (runtime) rejects non-merge set sentinels
        var writes = Parse("""[{"set":{"path":"c/a","fields":{"x":{"deleteField":true}},"merge":true}}]""");
        Assert.Same(DeleteFieldValue.Instance, Assert.IsType<SetWrite>(writes[0]).Fields["x"]);
    }

    [Fact]
    public void Precondition_WrongTypedExists_Throws()
    {
        // exists must be a bool; a string value is a JsonException
        Throws("""[{"delete":{"path":"c/a","precondition":{"exists":"yes"}}}]""");
    }

    [Fact]
    public void Precondition_WrongTypedUpdateTime_Throws()
    {
        // updateTime must be a string; a number is a JsonException
        Throws("""[{"delete":{"path":"c/a","precondition":{"updateTime":12345}}}]""");
    }

    [Fact]
    public void Set_NestedSentinel_InsideMapValue_Parses()
    {
        var writes = Parse("""
            [{"set":{"path":"c/a","merge":true,"fields":{"m":{"mapValue":{"fields":{"drop":{"deleteField":true},"keep":{"integerValue":"1"}}}}}}}]
            """);
        var set = Assert.IsType<SetWrite>(Assert.Single(writes));
        var m = Assert.IsType<MapValue>(set.Fields["m"]);
        Assert.Same(DeleteFieldValue.Instance, m.Fields["drop"]);
        Assert.Equal(new IntegerValue(1), m.Fields["keep"]);
    }
}
