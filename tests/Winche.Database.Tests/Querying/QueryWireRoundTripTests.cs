using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Ast.Serialization;

namespace Winche.Database.Tests.Querying;

public class QueryWireRoundTripTests
{
    [Fact]
    public void Offset_SurvivesWriteThenParse()
    {
        var q = new Query("c", Offset: 4);
        var json = QueryAstWriter.Write(q);
        Assert.Equal(4, (int?)json["offset"]);
        Assert.Equal(4, QueryParser.Parse(json).Offset);
    }

    [Fact]
    public void LimitToLast_SurvivesWriteThenParse()
    {
        var q = new Query("c", OrderBy: [new Ordering(FieldPath.Parse("i"))], LimitToLast: 3);
        var json = QueryAstWriter.Write(q);
        Assert.Equal(3, (int?)json["limitToLast"]);
        Assert.Equal(3, QueryParser.Parse(json).LimitToLast);
    }
}
