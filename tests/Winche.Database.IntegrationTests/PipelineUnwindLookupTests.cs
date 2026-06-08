using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class PipelineUnwindLookupTests(PostgresFixture fx) : PipelineTestBase(fx)
{
    // ── Unwind ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Unwind_OneRowPerElement()
    {
        await SeedDoc("a", new Dictionary<string, Value> { ["tags"] = new ArrayValue([S("x"), S("y")]) });

        var result = await RunPipeline(
            new Match("c", null),
            new Unwind(F("tags"), "tag"));

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal([S("x"), S("y")], result.Rows.Select(r => r["tag"]).OfType<StringValue>().OrderBy(s => s.Value).Cast<Value>());
    }

    [Fact]
    public async Task Unwind_MissingOrNonArray_DropsRow_UnlessPreserve()
    {
        await SeedDoc("hasArr", new Dictionary<string, Value> { ["tags"] = new ArrayValue([S("x")]) });
        await SeedDoc("noField", new Dictionary<string, Value> { ["other"] = I(1) });
        await SeedDoc("scalar", new Dictionary<string, Value> { ["tags"] = S("notArray") });

        var dropped = await RunPipeline(new Match("c", null), new Unwind(F("tags"), "tag"));
        Assert.Single(dropped.Rows);

        var preserved = await RunPipeline(new Match("c", null),
            new Unwind(F("tags"), "tag", PreserveNullAndEmpty: true));
        Assert.Equal(3, preserved.Rows.Count);
        Assert.Equal(2, preserved.Rows.Count(r => !r.ContainsKey("tag")));   // missing tag column
    }

    [Fact]
    public async Task Unwind_ElementsAreFullyTyped()
    {
        await SeedDoc("a", new Dictionary<string, Value>
        {
            ["items"] = new ArrayValue([new MapValue(new Dictionary<string, Value> { ["qty"] = I(3) })]),
        });

        var result = await RunPipeline(
            new Match("c", null),
            new Unwind(F("items"), "item"),
            new Where(new FieldFilter(F("item.qty"), FilterOperator.Gt, I(2))));

        Assert.Single(result.Rows);   // filter navigates INTO the unwound tagged element
    }

    // ── Lookup ───────────────────────────────────────────────────────────────

    private async Task SeedUsersAndOrders()
    {
        await SeedDoc("u1", new Dictionary<string, Value> { ["age"] = I(30) }, collection: "users");
        await SeedDoc("u2", new Dictionary<string, Value> { ["age"] = I(40) }, collection: "users");
        await SeedDoc("o1", new Dictionary<string, Value> { ["userId"] = new ReferenceValue("users/u1"), ["amt"] = I(10) }, collection: "orders");
        await SeedDoc("o2", new Dictionary<string, Value> { ["userId"] = new ReferenceValue("users/u2"), ["amt"] = I(20) }, collection: "orders");
        await SeedDoc("o3", new Dictionary<string, Value> { ["userId"] = new ReferenceValue("users/nope"), ["amt"] = I(30) }, collection: "orders");
    }

    [Fact]
    public async Task Lookup_JoinsByNameViaReference()
    {
        await SeedUsersAndOrders();

        var result = await RunPipeline(
            new Match("orders", null),
            new Lookup("users", F("userId"), F("__name__"), "user"),
            new Sort([new Ordering(F("amt"))]));

        Assert.Equal(3, result.Rows.Count);

        var o1User = Assert.IsType<ArrayValue>(result.Rows[0]["user"]);
        var joined = Assert.IsType<MapValue>(Assert.Single(o1User.Values));
        Assert.Equal(I(30), joined.Fields["age"]);
        Assert.Equal(new ReferenceValue("users/u1"), joined.Fields["__name__"]);

        var o3User = Assert.IsType<ArrayValue>(result.Rows[2]["user"]);
        Assert.Empty(o3User.Values);                              // no match → empty array
    }

    [Fact]
    public async Task Lookup_ValueJoin_WithFilterOrderLimit()
    {
        await SeedDoc("p1", new Dictionary<string, Value> { ["cat"] = S("a"), ["rank"] = I(2) }, collection: "products");
        await SeedDoc("p2", new Dictionary<string, Value> { ["cat"] = S("a"), ["rank"] = I(1) }, collection: "products");
        await SeedDoc("p3", new Dictionary<string, Value> { ["cat"] = S("a"), ["rank"] = I(3) }, collection: "products");
        await SeedDoc("p4", new Dictionary<string, Value> { ["cat"] = S("b"), ["rank"] = I(9) }, collection: "products");
        await SeedDoc("cat", new Dictionary<string, Value> { ["id"] = S("a") }, collection: "cats");

        var result = await RunPipeline(
            new Match("cats", null),
            new Lookup("products", F("id"), F("cat"), "top",
                Where: new FieldFilter(F("rank"), FilterOperator.Lt, I(3)),
                OrderBy: [new Ordering(F("rank"))],
                Limit: 1));

        var top = Assert.IsType<ArrayValue>(Assert.Single(result.Rows)["top"]);
        var winner = Assert.IsType<MapValue>(Assert.Single(top.Values));
        Assert.Equal(I(1), winner.Fields["rank"]);                // filtered (rank<3), ordered, limited
    }

    [Fact]
    public async Task Lookup_ThenUnwind_ThenFilterOnJoinedField()
    {
        await SeedUsersAndOrders();

        var result = await RunPipeline(
            new Match("orders", null),
            new Lookup("users", F("userId"), F("__name__"), "user"),
            new Unwind(F("user"), "u"),
            new Where(new FieldFilter(F("u.age"), FilterOperator.Gte, I(40))));

        var row = Assert.Single(result.Rows);
        Assert.Equal(I(20), row["amt"]);                          // only o2's user is 40+
    }

    // ── I3: Lookup orderBy is preserved inside the aggregated array ──────────

    [Fact]
    public async Task Lookup_OrderBy_IsPreservedInsideJoinedArray()
    {
        for (var i = 1; i <= 4; i++)
            await SeedDoc($"p{i}", new Dictionary<string, Value> { ["cat"] = S("a"), ["rank"] = I(5 - i) }, collection: "products");
        await SeedDoc("cat", new Dictionary<string, Value> { ["id"] = S("a") }, collection: "cats");

        var result = await RunPipeline(
            new Match("cats", null),
            new Lookup("products", F("id"), F("cat"), "top",
                OrderBy: [new Ordering(F("rank"))], Limit: 3));

        var arr = Assert.IsType<ArrayValue>(Assert.Single(result.Rows)["top"]);
        var ranks = arr.Values.Select(v => ((IntegerValue)((MapValue)v).Fields["rank"]).Value);
        Assert.Equal([1L, 2L, 3L], ranks);    // ascending, limited to 3 — order guaranteed
    }

    // ── M5: Unwind with empty array ──────────────────────────────────────────

    [Fact]
    public async Task Unwind_EmptyArray_DroppedUnlessPreserve()
    {
        await SeedDoc("e", new Dictionary<string, Value> { ["tags"] = new ArrayValue([]) });

        var dropped = await RunPipeline(new Match("c", null), new Unwind(F("tags"), "tag"));
        Assert.Empty(dropped.Rows);

        var preserved = await RunPipeline(new Match("c", null),
            new Unwind(F("tags"), "tag", PreserveNullAndEmpty: true));
        Assert.False(Assert.Single(preserved.Rows).ContainsKey("tag"));
    }
}
