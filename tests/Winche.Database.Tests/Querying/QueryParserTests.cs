// tests/Winche.Database.Tests/Querying/QueryParserTests.cs
using System.Text.Json.Nodes;
using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Values;

namespace Winche.Database.Tests.Querying;

public class QueryParserTests
{
    private static QueryAst Parse(string json) => QueryParser.Parse((JsonObject)JsonNode.Parse(json)!);

    [Fact]
    public void Minimal_ParsesCollectionOnly()
    {
        var q = Parse("""{"collection":"users"}""");
        Assert.Equal("users", q.Collection);
        Assert.Null(q.Where);
        Assert.Null(q.OrderBy);
        Assert.Null(q.Limit);
        Assert.Null(q.Start);
        Assert.Null(q.End);
    }

    [Fact]
    public void FieldFilter_Parses()
    {
        var q = Parse("""{"collection":"c","where":{"field":"age","op":"gt","value":{"integerValue":"21"}}}""");
        var f = Assert.IsType<FieldFilterAst>(q.Where);
        Assert.Equal(FieldPath.Parse("age"), f.Field);
        Assert.Equal(FilterOperator.Gt, f.Op);
        Assert.Equal(new IntegerValue(21), f.Operand);
    }

    [Fact]
    public void Composite_And_Or_Not_ParseRecursively()
    {
        var q = Parse("""
            {"collection":"c","where":{"and":[
                {"field":"a","op":"eq","value":{"booleanValue":true}},
                {"or":[
                    {"field":"b","op":"lt","value":{"doubleValue":1.5}},
                    {"not":{"unary":"isNull","field":"c"}}
                ]}
            ]}}
            """);
        var and = Assert.IsType<CompositeFilterAst>(q.Where);
        Assert.Equal(CompositeOp.And, and.Op);
        Assert.Equal(2, and.Filters.Count);
        var or = Assert.IsType<CompositeFilterAst>(and.Filters[1]);
        Assert.Equal(CompositeOp.Or, or.Op);
        var not = Assert.IsType<CompositeFilterAst>(or.Filters[1]);
        Assert.Equal(CompositeOp.Not, not.Op);
        var unary = Assert.IsType<UnaryFilterAst>(not.Filters[0]);
        Assert.Equal(UnaryOp.IsNull, unary.Op);
    }

    [Fact]
    public void Unary_AllThreeOps_Parse()
    {
        foreach (var (wire, expected) in new[] { ("isNull", UnaryOp.IsNull), ("isNan", UnaryOp.IsNan), ("exists", UnaryOp.Exists) })
        {
            var q = Parse($"{{\"collection\":\"c\",\"where\":{{\"unary\":\"{wire}\",\"field\":\"x\"}}}}");
            Assert.Equal(expected, Assert.IsType<UnaryFilterAst>(q.Where).Op);
        }
    }

    [Fact]
    public void Compare_Parses()
    {
        var q = Parse("""{"collection":"c","where":{"compare":{"left":"a.x","op":"gte","right":"b"}}}""");
        var c = Assert.IsType<FieldCompareAst>(q.Where);
        Assert.Equal(FieldPath.Parse("a.x"), c.Left);
        Assert.Equal(FilterOperator.Gte, c.Op);
        Assert.Equal(FieldPath.Parse("b"), c.Right);
    }

    [Theory]
    [InlineData("eq", FilterOperator.Eq)]
    [InlineData("ne", FilterOperator.Ne)]
    [InlineData("in", FilterOperator.In)]
    [InlineData("notIn", FilterOperator.NotIn)]
    [InlineData("arrayContains", FilterOperator.ArrayContains)]
    [InlineData("arrayContainsAny", FilterOperator.ArrayContainsAny)]
    [InlineData("arrayContainsAll", FilterOperator.ArrayContainsAll)]
    [InlineData("contains", FilterOperator.Contains)]
    [InlineData("startsWith", FilterOperator.StartsWith)]
    [InlineData("endsWith", FilterOperator.EndsWith)]
    [InlineData("regex", FilterOperator.Regex)]
    public void AllOperators_Parse(string wire, FilterOperator expected)
    {
        var q = Parse($"{{\"collection\":\"c\",\"where\":{{\"field\":\"f\",\"op\":\"{wire}\",\"value\":{{\"stringValue\":\"x\"}}}}}}");

        Assert.Equal(expected, Assert.IsType<FieldFilterAst>(q.Where).Op);
    }

    [Fact]
    public void OrderByLimitCursors_Parse()
    {
        var q = Parse("""
            {"collection":"c",
             "orderBy":[{"field":"age","direction":"desc"},{"field":"name"}],
             "limit":25,
             "start":{"values":[{"integerValue":"30"}],"before":false},
             "end":{"values":[{"integerValue":"18"},{"stringValue":"z"}],"before":true}}
            """);
        Assert.Equal(2, q.OrderBy!.Count);
        Assert.Equal(SortDirection.Desc, q.OrderBy[0].Direction);
        Assert.Equal(SortDirection.Asc, q.OrderBy[1].Direction);   // default
        Assert.Equal(25, q.Limit);
        Assert.False(q.Start!.Before);
        Assert.Equal([new IntegerValue(30)], q.Start.Values);
        Assert.True(q.End!.Before);
        Assert.Equal(2, q.End.Values.Count);
    }

    [Theory]
    [InlineData("""{"where":{}}""", "$.collection")]                                            // missing collection
    [InlineData("""{"collection":"c","where":{"field":"f","op":"bogus","value":{"nullValue":null}}}""", "$.where.op")]
    [InlineData("""{"collection":"c","where":{"field":"f","op":"eq"}}""", "$.where.value")]      // missing value
    [InlineData("""{"collection":"c","where":{"and":"notArray"}}""", "$.where.and")]
    [InlineData("""{"collection":"c","where":{"unary":"bogus","field":"f"}}""", "$.where.unary")]
    [InlineData("""{"collection":"c","where":{"field":"f"}}""", "$.where")]                      // not a recognized shape
    [InlineData("""{"collection":"c","orderBy":[{"direction":"asc"}]}""", "$.orderBy[0].field")]
    [InlineData("""{"collection":"c","start":{"before":true}}""", "$.start.values")]
    [InlineData("""{"collection":"c","limit":"ten"}""", "$.limit")]
    public void BadInput_ThrowsWithJsonPath(string json, string expectedPath)
    {
        var ex = Assert.Throws<QueryParseException>(() => Parse(json));
        Assert.Equal(expectedPath, ex.JsonPath);
    }

    [Fact]
    public void BadTaggedValue_PathPointsIntoValue()
    {
        var ex = Assert.Throws<QueryParseException>(() =>
            Parse("""{"collection":"c","where":{"and":[{"field":"f","op":"eq","value":{"bogusValue":1}}]}}"""));
        Assert.Equal("$.where.and[0].value", ex.JsonPath);
    }

    [Fact]
    public void MultiShapeFilter_Throws()
    {
        var ex = Assert.Throws<QueryParseException>(() =>
            Parse("""{"collection":"c","where":{"and":[],"field":"f","op":"eq","value":{"nullValue":null}}}"""));
        Assert.Equal("$.where", ex.JsonPath);
    }
}
