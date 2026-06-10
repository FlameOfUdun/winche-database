using System.Text.Json.Nodes;
using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Ast.Serialization;
using Winche.Database.Values;

namespace Winche.Database.Tests.Querying;

/// <summary>Tests for Query.Select JSON round-trip (parse ↔ write).</summary>
public class QuerySelectSerializationTests
{
    private static Query Parse(string json) =>
        QueryParser.Parse((JsonObject)JsonNode.Parse(json)!);

    private static Query RoundTrip(Query q)
    {
        var json = QueryAstWriter.Write(q).ToJsonString();
        return Parse(json);
    }

    // ── Parse ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_NoSelect_SelectIsNull()
    {
        var q = Parse("""{"collection":"c"}""");
        Assert.Null(q.Select);
    }

    [Fact]
    public void Parse_SelectSingleField()
    {
        var q = Parse("""{"collection":"c","select":["name"]}""");
        Assert.NotNull(q.Select);
        Assert.Single(q.Select);
        Assert.Equal(FieldPath.Parse("name"), q.Select[0]);
    }

    [Fact]
    public void Parse_SelectMultipleFields()
    {
        var q = Parse("""{"collection":"c","select":["name","age","email"]}""");
        Assert.NotNull(q.Select);
        Assert.Equal(3, q.Select.Count);
        Assert.Equal(FieldPath.Parse("name"),  q.Select[0]);
        Assert.Equal(FieldPath.Parse("age"),   q.Select[1]);
        Assert.Equal(FieldPath.Parse("email"), q.Select[2]);
    }

    [Fact]
    public void Parse_SelectNestedPath()
    {
        var q = Parse("""{"collection":"c","select":["address.city","address.zip"]}""");
        Assert.NotNull(q.Select);
        Assert.Equal(2, q.Select.Count);
        Assert.Equal(FieldPath.Parse("address.city"), q.Select[0]);
        Assert.Equal(FieldPath.Parse("address.zip"),  q.Select[1]);
    }

    [Fact]
    public void Parse_SelectNotArray_ThrowsWithPath()
    {
        var ex = Assert.Throws<QueryParseException>(() =>
            Parse("""{"collection":"c","select":"name"}"""));
        Assert.Equal("$.select", ex.JsonPath);
    }

    [Fact]
    public void Parse_SelectContainsNonString_ThrowsWithPath()
    {
        var ex = Assert.Throws<QueryParseException>(() =>
            Parse("""{"collection":"c","select":[123]}"""));
        Assert.Equal("$.select[0]", ex.JsonPath);
    }

    [Fact]
    public void Parse_SelectContainsEmptyPath_ThrowsWithPath()
    {
        var ex = Assert.Throws<QueryParseException>(() =>
            Parse("""{"collection":"c","select":[""]}"""));
        Assert.Equal("$.select[0]", ex.JsonPath);
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Write_NoSelect_SelectKeyAbsent()
    {
        var q = new Query("c");
        var obj = QueryAstWriter.Write(q);
        Assert.False(obj.ContainsKey("select"));
    }

    [Fact]
    public void Write_WithSelect_SelectKeyPresent()
    {
        var q = new Query("c", Select: [FieldPath.Parse("name"), FieldPath.Parse("address.city")]);
        var obj = QueryAstWriter.Write(q);
        Assert.True(obj.ContainsKey("select"));
        var arr = Assert.IsType<JsonArray>(obj["select"]);
        Assert.Equal(2, arr.Count);
        Assert.Equal("name",         arr[0]!.GetValue<string>());
        Assert.Equal("address.city", arr[1]!.GetValue<string>());
    }

    // ── Round-trip ────────────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_SelectPreserved()
    {
        var q = new Query("users",
            Where: new FieldFilter(FieldPath.Parse("active"), FilterOperator.Eq, new BooleanValue(true)),
            Select: [FieldPath.Parse("name"), FieldPath.Parse("address.city")]);

        var q2 = RoundTrip(q);

        Assert.NotNull(q2.Select);
        Assert.Equal(2, q2.Select.Count);
        Assert.Equal(FieldPath.Parse("name"),         q2.Select[0]);
        Assert.Equal(FieldPath.Parse("address.city"), q2.Select[1]);
    }

    [Fact]
    public void RoundTrip_NoSelect_StillNull()
    {
        var q = new Query("users");
        var q2 = RoundTrip(q);
        Assert.Null(q2.Select);
    }

    // ── Equality ──────────────────────────────────────────────────────────────

    [Fact]
    public void Equality_SameSelect_Equal()
    {
        var q1 = new Query("c", Select: [FieldPath.Parse("a"), FieldPath.Parse("b.c")]);
        var q2 = new Query("c", Select: [FieldPath.Parse("a"), FieldPath.Parse("b.c")]);
        Assert.Equal(q1, q2);
    }

    [Fact]
    public void Equality_DifferentSelect_NotEqual()
    {
        var q1 = new Query("c", Select: [FieldPath.Parse("a")]);
        var q2 = new Query("c", Select: [FieldPath.Parse("b")]);
        Assert.NotEqual(q1, q2);
    }

    [Fact]
    public void Equality_SelectVsNoSelect_NotEqual()
    {
        var q1 = new Query("c", Select: [FieldPath.Parse("a")]);
        var q2 = new Query("c");
        Assert.NotEqual(q1, q2);
    }
}
