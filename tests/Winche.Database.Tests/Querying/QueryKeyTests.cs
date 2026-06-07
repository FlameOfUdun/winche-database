// tests/Winche.Database.Tests/Querying/QueryKeyTests.cs
using System.Text.Json;
using Winche.Database.Querying;
using Winche.Database.Querying.Ast;

namespace Winche.Database.Tests.Querying;

public class QueryKeyTests
{
    private static QueryAst Q(string json) => JsonSerializer.Deserialize<QueryAst>(json)!;

    [Fact]
    public void IdenticalQueries_SameKey()
    {
        const string json = """{"collection":"c","where":{"field":"a","op":"eq","value":{"integerValue":"1"}},"limit":10}""";
        Assert.Equal(QueryKey.Compute(Q(json)), QueryKey.Compute(Q(json)));
    }

    [Fact]
    public void DifferentLimit_DifferentKey()
    {
        Assert.NotEqual(
            QueryKey.Compute(Q("""{"collection":"c","limit":10}""")),
            QueryKey.Compute(Q("""{"collection":"c","limit":11}""")));
    }

    [Fact]
    public void DifferentValueType_DifferentKey()
    {
        Assert.NotEqual(
            QueryKey.Compute(Q("""{"collection":"c","where":{"field":"a","op":"eq","value":{"integerValue":"1"}}}""")),
            QueryKey.Compute(Q("""{"collection":"c","where":{"field":"a","op":"eq","value":{"doubleValue":1}}}""")));
    }
}
