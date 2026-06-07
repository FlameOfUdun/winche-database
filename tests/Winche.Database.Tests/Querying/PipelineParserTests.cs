// tests/Winche.Database.Tests/Querying/PipelineParserTests.cs
using System.Text.Json.Nodes;
using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Values;

namespace Winche.Database.Tests.Querying;

public class PipelineParserTests
{
    private static PipelineAst Parse(string json) => PipelineParser.Parse((JsonObject)JsonNode.Parse(json)!);

    [Fact]
    public void Match_Parses()
    {
        var p = Parse("""{"pipeline":[{"match":{"collection":"orders","where":{"field":"s","op":"eq","value":{"booleanValue":true}}}}]}""");
        var m = Assert.IsType<MatchStageAst>(Assert.Single(p.Stages));
        Assert.Equal("orders", m.Collection);
        Assert.IsType<FieldFilterAst>(m.Where);
    }

    [Fact]
    public void Match_WhereOptional()
    {
        var p = Parse("""{"pipeline":[{"match":{"collection":"orders"}}]}""");
        Assert.Null(Assert.IsType<MatchStageAst>(p.Stages[0]).Where);
    }

    [Fact]
    public void Lookup_ParsesAllFields_AndDefaults()
    {
        var p = Parse("""
            {"pipeline":[{"match":{"collection":"o"}},
            {"lookup":{"collection":"users","localField":"userId","foreignField":"__name__","as":"user"}}]}
            """);
        var l = Assert.IsType<LookupStageAst>(p.Stages[1]);
        Assert.Equal("users", l.Collection);
        Assert.Equal(FieldPath.Parse("userId"), l.LocalField);
        Assert.Equal(FieldPath.Parse("__name__"), l.ForeignField);
        Assert.Equal("user", l.As);
        Assert.Null(l.Where);
        Assert.Null(l.OrderBy);
        Assert.Equal(100, l.Limit);
    }

    [Fact]
    public void Unwind_Group_Project_Sort_Limit_Skip_Filter_Parse()
    {
        var p = Parse("""
            {"pipeline":[
              {"match":{"collection":"o"}},
              {"unwind":{"field":"items","as":"item","preserveNullAndEmpty":true}},
              {"filter":{"field":"item","op":"eq","value":{"integerValue":"1"}}},
              {"group":{"keys":[{"as":"k","field":"city"}],
                        "accumulators":[{"as":"n","fn":"count"},{"as":"t","fn":"sum","field":"amt"}],
                        "having":{"field":"n","op":"gt","value":{"integerValue":"0"}}}},
              {"project":{"fields":[{"as":"k","field":"k"},{"as":"c","value":{"stringValue":"x"}},{"as":"g","fn":"avg","field":"t"}]}},
              {"sort":[{"field":"t","direction":"desc"}]},
              {"limit":10},
              {"skip":5}
            ]}
            """);
        Assert.Equal(8, p.Stages.Count);

        var u = Assert.IsType<UnwindStageAst>(p.Stages[1]);
        Assert.True(u.PreserveNullAndEmpty);

        Assert.IsType<FilterStageAst>(p.Stages[2]);

        var g = Assert.IsType<GroupStageAst>(p.Stages[3]);
        Assert.Single(g.Keys);
        Assert.Equal(2, g.Accumulators.Count);
        Assert.Equal(AggFunction.Count, g.Accumulators[0].Fn);
        Assert.Null(g.Accumulators[0].Field);
        Assert.Equal(AggFunction.Sum, g.Accumulators[1].Fn);
        Assert.NotNull(g.Having);

        var pr = Assert.IsType<ProjectStageAst>(p.Stages[4]);
        Assert.IsType<FieldRefExprAst>(pr.Fields[0].Expr);
        Assert.Equal(new StringValue("x"), Assert.IsType<LiteralExprAst>(pr.Fields[1].Expr).Value);
        Assert.Equal(AggFunction.Avg, Assert.IsType<AggFuncExprAst>(pr.Fields[2].Expr).Fn);

        var s = Assert.IsType<SortStageAst>(p.Stages[5]);
        Assert.Equal(SortDirection.Desc, s.Fields[0].Direction);

        Assert.Equal(10, Assert.IsType<LimitStageAst>(p.Stages[6]).Count);
        Assert.Equal(5, Assert.IsType<SkipStageAst>(p.Stages[7]).Count);
    }

    [Theory]
    [InlineData("""{"pipeline":"x"}""", "$.pipeline")]
    [InlineData("""{"pipeline":[{"bogus":{}}]}""", "$.pipeline[0]")]
    [InlineData("""{"pipeline":[{"match":{}}]}""", "$.pipeline[0].match.collection")]
    [InlineData("""{"pipeline":[{"match":{"collection":"c"}},{"lookup":{"collection":"u","localField":"a","as":"x"}}]}""", "$.pipeline[1].lookup.foreignField")]
    [InlineData("""{"pipeline":[{"match":{"collection":"c"}},{"group":{"keys":[],"accumulators":[{"as":"n","fn":"bogus"}]}}]}""", "$.pipeline[1].group.accumulators[0].fn")]
    [InlineData("""{"pipeline":[{"match":{"collection":"c"}},{"limit":"x"}]}""", "$.pipeline[1].limit")]
    [InlineData("""{"pipeline":[{"match":{"collection":"c"}},{"project":{"fields":[{"as":"x"}]}}]}""", "$.pipeline[1].project.fields[0]")]
    public void BadInput_ThrowsWithJsonPath(string json, string expectedPath)
    {
        var ex = Assert.Throws<QueryParseException>(() => Parse(json));
        Assert.Equal(expectedPath, ex.JsonPath);
    }

    [Fact]
    public void MultiShapeStage_Throws()
    {
        var ex = Assert.Throws<QueryParseException>(() =>
            Parse("""{"pipeline":[{"match":{"collection":"c"},"limit":1}]}"""));
        Assert.Equal("$.pipeline[0]", ex.JsonPath);
    }

    // ── M4: Unknown sibling keys in a stage object ───────────────────────────

    [Fact]
    public void UnknownSiblingKey_Throws()
    {
        var ex = Assert.Throws<QueryParseException>(() =>
            Parse("""{"pipeline":[{"match":{"collection":"c"},"typo":1}]}"""));
        Assert.Equal("$.pipeline[0]", ex.JsonPath);
    }
}
