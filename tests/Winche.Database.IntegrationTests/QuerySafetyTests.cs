using Winche.Database.Querying.Ast;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class QuerySafetyTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private const string Evil = "x'; DROP TABLE documents; --";

    [Fact]
    public async Task InjectionViaFieldPath_IsInert()
    {
        await Seed("a", new IntegerValue(1));
        var ids = await Ids(new Query("c",
            Where: new FieldFilter(F(Evil), FilterOperator.Eq, new IntegerValue(1))));
        Assert.Equal([], ids);                 // no such field; and table still exists:
        Assert.Equal(["a"], await Ids(new Query("c")));
    }

    [Fact]
    public async Task InjectionViaStringOperand_IsInert()
    {
        await Seed("a", new StringValue(Evil));
        Assert.Equal(["a"], await Filter(FilterOperator.Eq, new StringValue(Evil)));
    }

    [Fact]
    public async Task InjectionViaCollectionName_IsInert()
    {
        await Seed("a", new IntegerValue(1));
        Assert.Equal([], await Ids(new Query(Evil)));
        Assert.Equal(["a"], await Ids(new Query("c")));
    }

    [Fact]
    public async Task InjectionViaOrderByField_IsInert()
    {
        await Seed("a", new IntegerValue(1));
        var ids = await Ids(new Query("c", OrderBy: [new Ordering(F(Evil))]));
        Assert.Equal([], ids);                 // implicit Exists on a nonexistent field
        Assert.Equal(["a"], await Ids(new Query("c")));
    }

    [Fact]
    public async Task InjectionViaCursorValue_IsInert()
    {
        await Seed("a", new StringValue("z"));
        var ids = await Ids(new Query("c",
            OrderBy: [new Ordering(F("f"))],
            Start: new Cursor([new StringValue(Evil)], Before: true)));
        Assert.Equal(["a"], ids);              // Evil string sorts before "z"; table intact
    }

    [Fact]
    public async Task RegexOperand_CannotEscapeParameter()
    {
        await Seed("a", new StringValue("abc"));
        Assert.Equal([], await Filter(FilterOperator.Regex, new StringValue("'; DROP TABLE documents; --")));
        Assert.Equal(["a"], await Ids(new Query("c")));
    }
}
