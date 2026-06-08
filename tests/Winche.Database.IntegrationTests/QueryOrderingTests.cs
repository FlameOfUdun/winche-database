using Winche.Database.Querying.Ast;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class QueryOrderingTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private Task<List<string>> OrderedBy(string field, SortDirection dir = SortDirection.Asc) =>
        Ids(new Query("c", OrderBy: [new Ordering(F(field), dir)]));

    [Fact]
    public async Task CrossTypeOrder_ThroughTheFullQueryPath()
    {
        await Seed("e_map", new MapValue(new Dictionary<string, Value> { ["a"] = new IntegerValue(1) }));
        await Seed("a_bool", new BooleanValue(true));
        await Seed("d_arr", new ArrayValue([new IntegerValue(1)]));
        await Seed("b_num", new IntegerValue(3));
        await Seed("c_str", new StringValue("s"));

        Assert.Equal(["a_bool", "b_num", "c_str", "d_arr", "e_map"], await OrderedBy("f"));
        Assert.Equal(["e_map", "d_arr", "c_str", "b_num", "a_bool"], await OrderedBy("f", SortDirection.Desc));
    }

    [Fact]
    public async Task MissingOrderByField_ExcludesDocument()
    {
        await Seed("has", new IntegerValue(1));
        await SeedDoc("not", new Dictionary<string, Value> { ["other"] = new IntegerValue(1) });
        Assert.Equal(["has"], await OrderedBy("f"));                       // rule 6
    }

    [Fact]
    public async Task ExplicitNullSortsFirst_ButIsNotExcluded()
    {
        await Seed("n", new NullValue());
        await Seed("v", new IntegerValue(1));
        Assert.Equal(["n", "v"], await OrderedBy("f"));
    }

    [Fact]
    public async Task EqualValues_TieBrokenByName_FollowingLastDirection()
    {
        await Seed("b", new IntegerValue(1));
        await Seed("a", new IntegerValue(1));
        Assert.Equal(["a", "b"], await OrderedBy("f"));                    // ASC → __name__ ASC
        Assert.Equal(["b", "a"], await OrderedBy("f", SortDirection.Desc)); // DESC → __name__ DESC (rule 7)
    }

    [Fact]
    public async Task MultiKeySort_MixedDirections()
    {
        await SeedDoc("x1", new Dictionary<string, Value> { ["g"] = new IntegerValue(1), ["v"] = new IntegerValue(10) });
        await SeedDoc("x2", new Dictionary<string, Value> { ["g"] = new IntegerValue(1), ["v"] = new IntegerValue(20) });
        await SeedDoc("x3", new Dictionary<string, Value> { ["g"] = new IntegerValue(2), ["v"] = new IntegerValue(5) });

        var ids = await Ids(new Query("c",
            OrderBy: [new Ordering(F("g")), new Ordering(F("v"), SortDirection.Desc)]));
        Assert.Equal(["x2", "x1", "x3"], ids);
    }

    [Fact]
    public async Task OrderByArrayField_ElementWise()
    {
        await Seed("a2", new ArrayValue([new IntegerValue(1), new IntegerValue(2)]));
        await Seed("a1", new ArrayValue([new IntegerValue(1)]));
        await Seed("a3", new ArrayValue([new IntegerValue(2)]));
        Assert.Equal(["a1", "a2", "a3"], await OrderedBy("f"));
    }

    [Fact]
    public async Task OrderByNestedField_Works()
    {
        await SeedDoc("u2", new Dictionary<string, Value>
            { ["m"] = new MapValue(new Dictionary<string, Value> { ["x"] = new IntegerValue(2) }) });
        await SeedDoc("u1", new Dictionary<string, Value>
            { ["m"] = new MapValue(new Dictionary<string, Value> { ["x"] = new IntegerValue(1) }) });
        Assert.Equal(["u1", "u2"], await Ids(new Query("c", OrderBy: [new Ordering(F("m.x"))])));
    }
}
