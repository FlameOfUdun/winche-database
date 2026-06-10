using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

/// <summary>
/// Integration tests that prove the SQL-level projection generates the same observable results
/// as the former post-fetch <c>FieldProjector</c>, plus exercises edge-cases unique to the
/// push-down path (ancestor/descendant precedence, path-through-non-map, deep nesting,
/// whole-map subtree selection).
/// </summary>
[Collection("postgres")]
public class QueryProjectionSqlTests(PostgresFixture fx) : QueryTestBase(fx)
{
    // ── Ancestor wins: selecting both a whole map and one of its children ────

    [Fact]
    public async Task AncestorAndDescendantSelected_WholeAncestorReturned()
    {
        // Selecting "address" (whole map) AND "address.city" (a child path).
        // The ancestor should win — the full address map must be returned.
        await SeedDoc("d1", new Dictionary<string, Value>
        {
            ["address"] = new MapValue(new Dictionary<string, Value>
            {
                ["city"]    = new StringValue("Stavanger"),
                ["country"] = new StringValue("Norway"),
            }),
            ["name"] = new StringValue("Alice"),
        });

        var result = await Run(new Query("c", Select: [F("address"), F("address.city")]));

        var doc = Assert.Single(result.Documents);
        // "name" must NOT be present (not selected).
        Assert.False(doc.Fields.ContainsKey("name"));
        // "address" must be present and contain BOTH fields (whole map).
        var addr = Assert.IsType<MapValue>(doc.Fields["address"]);
        Assert.Equal(2, addr.Fields.Count);
        Assert.Equal(new StringValue("Stavanger"), addr.Fields["city"]);
        Assert.Equal(new StringValue("Norway"),    addr.Fields["country"]);
    }

    [Fact]
    public async Task DescendantListedBeforeAncestor_AncestorStillWins()
    {
        // Order of the Select list must not affect the ancestor-wins rule.
        await SeedDoc("d1", new Dictionary<string, Value>
        {
            ["address"] = new MapValue(new Dictionary<string, Value>
            {
                ["city"]    = new StringValue("Bergen"),
                ["country"] = new StringValue("Norway"),
            }),
        });

        // Descendant first in the list — ancestor should still win.
        var result = await Run(new Query("c", Select: [F("address.city"), F("address")]));

        var doc = Assert.Single(result.Documents);
        var addr = Assert.IsType<MapValue>(doc.Fields["address"]);
        Assert.Equal(2, addr.Fields.Count);
    }

    // ── Path through a non-map value → silently omitted ──────────────────────

    [Fact]
    public async Task PathThroughNonMap_SilentlyOmitted_NoError()
    {
        // "age" is an IntegerValue; selecting "age.foo" is a path through a non-map.
        // The field must be absent in the result — no error.
        await SeedDoc("d1", new Dictionary<string, Value>
        {
            ["age"]  = new IntegerValue(42),
            ["name"] = new StringValue("Bob"),
        });

        var result = await Run(new Query("c", Select: [F("age.foo")]));

        var doc = Assert.Single(result.Documents);
        // "age.foo" is through a non-map → omitted; "name" was not selected → omitted.
        Assert.Empty(doc.Fields);
        // Document metadata preserved.
        Assert.Equal("d1",  doc.Id);
        Assert.Equal("c/d1", doc.Path);
    }

    [Fact]
    public async Task MixedPaths_SomeValidSomeThroughNonMap_OnlyValidReturned()
    {
        await SeedDoc("d1", new Dictionary<string, Value>
        {
            ["score"]   = new IntegerValue(100),
            ["address"] = new MapValue(new Dictionary<string, Value>
            {
                ["city"] = new StringValue("Oslo"),
            }),
        });

        // "score.sub" is through non-map (omitted); "address.city" is valid.
        var result = await Run(new Query("c", Select: [F("score.sub"), F("address.city")]));

        var doc = Assert.Single(result.Documents);
        Assert.False(doc.Fields.ContainsKey("score"));
        var addr = Assert.IsType<MapValue>(doc.Fields["address"]);
        Assert.Single(addr.Fields);
        Assert.Equal(new StringValue("Oslo"), addr.Fields["city"]);
    }

    // ── 3-level deep nesting ─────────────────────────────────────────────────

    [Fact]
    public async Task ThreeLevelDeepNesting_ReconstructedCorrectly()
    {
        await SeedDoc("d1", new Dictionary<string, Value>
        {
            ["a"] = new MapValue(new Dictionary<string, Value>
            {
                ["b"] = new MapValue(new Dictionary<string, Value>
                {
                    ["c"] = new StringValue("deep-value"),
                    ["d"] = new StringValue("other"),
                }),
                ["e"] = new StringValue("sibling"),
            }),
            ["top"] = new StringValue("root-level"),
        });

        var result = await Run(new Query("c", Select: [F("a.b.c")]));

        var doc = Assert.Single(result.Documents);
        // Only "a" at the top level.
        Assert.Single(doc.Fields);
        var aMap = Assert.IsType<MapValue>(doc.Fields["a"]);
        // Only "b" under "a".
        Assert.Single(aMap.Fields);
        var bMap = Assert.IsType<MapValue>(aMap.Fields["b"]);
        // Only "c" under "b".
        Assert.Single(bMap.Fields);
        Assert.Equal(new StringValue("deep-value"), bMap.Fields["c"]);
    }

    // ── Whole map subtree selected ────────────────────────────────────────────

    [Fact]
    public async Task SelectWholeMapSubtree_AllChildrenReturned()
    {
        await SeedDoc("d1", new Dictionary<string, Value>
        {
            ["profile"] = new MapValue(new Dictionary<string, Value>
            {
                ["firstName"] = new StringValue("Carol"),
                ["lastName"]  = new StringValue("Smith"),
                ["age"]       = new IntegerValue(30),
            }),
            ["secret"] = new StringValue("hidden"),
        });

        var result = await Run(new Query("c", Select: [F("profile")]));

        var doc = Assert.Single(result.Documents);
        Assert.False(doc.Fields.ContainsKey("secret"));
        var profile = Assert.IsType<MapValue>(doc.Fields["profile"]);
        Assert.Equal(3, profile.Fields.Count);
        Assert.Equal(new StringValue("Carol"),  profile.Fields["firstName"]);
        Assert.Equal(new StringValue("Smith"),  profile.Fields["lastName"]);
        Assert.Equal(new IntegerValue(30),      profile.Fields["age"]);
    }

    // ── Mixed multiple docs, absent paths ────────────────────────────────────

    [Fact]
    public async Task DeepSelect_SomeDocsMissingIntermediateNode_AbsentDocOmitsField()
    {
        // d1 has address.city; d2 has no address at all.
        await SeedDoc("d1", new Dictionary<string, Value>
        {
            ["address"] = new MapValue(new Dictionary<string, Value>
            {
                ["city"] = new StringValue("Trondheim"),
            }),
        });
        await SeedDoc("d2", new Dictionary<string, Value>
        {
            ["name"] = new StringValue("Dave"),
        });

        var result = await Run(new Query("c", Select: [F("address.city")]));

        Assert.Equal(2, result.Documents.Count);
        var d1 = result.Documents.Single(d => d.Id == "d1");
        var d2 = result.Documents.Single(d => d.Id == "d2");

        var addr = Assert.IsType<MapValue>(d1.Fields["address"]);
        Assert.Equal(new StringValue("Trondheim"), addr.Fields["city"]);

        Assert.Empty(d2.Fields); // address absent → nothing returned
    }
}
