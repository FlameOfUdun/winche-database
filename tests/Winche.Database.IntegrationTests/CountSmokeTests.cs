using Winche.Database.Querying.Ast;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class CountSmokeTests(PostgresFixture fx) : QueryTestBase(fx)
{
    [Fact]
    public async Task NoFilter_CountsWholeCollection_ExcludesOthers()
    {
        await Seed("a", new IntegerValue(1));
        await Seed("b", new IntegerValue(2));
        await SeedDoc("other", new Dictionary<string, Value>(), collection: "elsewhere");

        Assert.Equal(2, await Count(new Query("c")));
    }

    [Fact]
    public async Task EmptyCollection_CountsZero()
    {
        Assert.Equal(0, await Count(new Query("c")));
    }

    [Fact]
    public async Task Where_CountsOnlyMatches()
    {
        for (var i = 1; i <= 5; i++)
            await Seed($"d{i}", new IntegerValue(i));

        var count = await Count(new Query("c",
            Where: new FieldFilter(F("f"), FilterOperator.Gte, new IntegerValue(3))));
        Assert.Equal(3, count);                          // d3, d4, d5
    }

    [Fact]
    public async Task ExplicitLimit_CapsTheCount()
    {
        for (var i = 1; i <= 5; i++)
            await Seed($"d{i}", new IntegerValue(i));

        Assert.Equal(2, await Count(new Query("c", Limit: 2)));   // capped
        Assert.Equal(5, await Count(new Query("c")));            // full match, default page size NOT applied
    }
}
