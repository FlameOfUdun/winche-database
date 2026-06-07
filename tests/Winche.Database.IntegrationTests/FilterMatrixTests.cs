using Winche.Database.Querying.Ast;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class FilterMatrixTests(PostgresFixture fx) : QueryTestBase(fx)
{
    /// <summary>One doc per type-class, ids prefixed for readable assertions.</summary>
    private async Task SeedZoo()
    {
        await Seed("null", new NullValue());
        await Seed("false", new BooleanValue(false));
        await Seed("true", new BooleanValue(true));
        await Seed("nan", new DoubleValue(double.NaN));
        await Seed("int5", new IntegerValue(5));
        await Seed("dbl5", new DoubleValue(5.0));
        await Seed("dbl7", new DoubleValue(7.5));
        await Seed("ts", new TimestampValue(DateTimeOffset.UnixEpoch));
        await Seed("strA", new StringValue("Apple"));
        await Seed("strB", new StringValue("banana"));
        await Seed("bytes", new BytesValue([1, 2]));
        await Seed("ref", new ReferenceValue("users/u1"));
        await Seed("geo", new GeoPointValue(10, 20));
        await Seed("arr", new ArrayValue([new IntegerValue(1), new StringValue("x")]));
        await Seed("map", new MapValue(new Dictionary<string, Value> { ["k"] = new IntegerValue(1) }));
        await SeedDoc("missing", new Dictionary<string, Value> { ["other"] = new IntegerValue(1) });
    }

    // ── Eq (rule 2) ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Eq_IntMatchesEquivalentDouble()
    {
        await SeedZoo();
        Assert.Equal(["dbl5", "int5"], (await Filter(FilterOperator.Eq, new IntegerValue(5))).Order());
        Assert.Equal(["dbl5", "int5"], (await Filter(FilterOperator.Eq, new DoubleValue(5.0))).Order());
    }

    [Fact]
    public async Task Eq_Null_MatchesOnlyExplicitNull()
    {
        await SeedZoo();
        Assert.Equal(["null"], await Filter(FilterOperator.Eq, new NullValue()));   // NOT "missing"
    }

    [Fact]
    public async Task Eq_NaN_MatchesOnlyNaN()
    {
        await SeedZoo();
        Assert.Equal(["nan"], await Filter(FilterOperator.Eq, new DoubleValue(double.NaN)));
    }

    [Fact]
    public async Task Eq_EveryScalarClass_MatchesItself()
    {
        await SeedZoo();
        Assert.Equal(["true"], await Filter(FilterOperator.Eq, new BooleanValue(true)));
        Assert.Equal(["ts"], await Filter(FilterOperator.Eq, new TimestampValue(DateTimeOffset.UnixEpoch)));
        Assert.Equal(["strA"], await Filter(FilterOperator.Eq, new StringValue("Apple")));
        Assert.Equal(["bytes"], await Filter(FilterOperator.Eq, new BytesValue([1, 2])));
        Assert.Equal(["ref"], await Filter(FilterOperator.Eq, new ReferenceValue("users/u1")));
        Assert.Equal(["geo"], await Filter(FilterOperator.Eq, new GeoPointValue(10, 20)));
    }

    [Fact]
    public async Task Eq_ArrayAndMap_ElementWiseWithNumericEquivalence()
    {
        await SeedZoo();
        // int 1 in stored array matches double 1.0 in operand (rule 2)
        Assert.Equal(["arr"], await Filter(FilterOperator.Eq,
            new ArrayValue([new DoubleValue(1.0), new StringValue("x")])));
        Assert.Equal(["map"], await Filter(FilterOperator.Eq,
            new MapValue(new Dictionary<string, Value> { ["k"] = new DoubleValue(1.0) })));
    }

    // ── Inequalities (rule 1) ────────────────────────────────────────────────

    [Fact]
    public async Task Gt_Number_MatchesOnlyNumbers_NaNAndOthersExcluded()
    {
        await SeedZoo();
        Assert.Equal(["dbl7"], await Filter(FilterOperator.Gt, new IntegerValue(5)));
        Assert.Equal(["dbl5", "dbl7", "int5"], (await Filter(FilterOperator.Gte, new IntegerValue(5))).Order());
    }

    [Fact]
    public async Task Lt_String_MatchesOnlyStrings_ByteOrder()
    {
        await SeedZoo();
        // UTF-8 byte order: "Apple" (A=0x41) < "banana" (b=0x62)
        Assert.Equal(["strA"], await Filter(FilterOperator.Lt, new StringValue("banana")));
        Assert.Equal([], await Filter(FilterOperator.Lt, new StringValue("Apple")));
    }

    [Fact]
    public async Task Gt_Bool_FalseLessThanTrue()
    {
        await SeedZoo();
        Assert.Equal(["true"], await Filter(FilterOperator.Gt, new BooleanValue(false)));
    }

    [Fact]
    public async Task Inequality_WithNaN_MatchesNothing()
    {
        await SeedZoo();
        Assert.Equal([], await Filter(FilterOperator.Gt, new DoubleValue(double.NaN)));
        Assert.Equal([], await Filter(FilterOperator.Lte, new DoubleValue(double.NaN)));
    }

    [Fact]
    public async Task Gt_GeoPoint_LatitudeThenLongitude()
    {
        await SeedZoo();
        Assert.Equal(["geo"], await Filter(FilterOperator.Gt, new GeoPointValue(10, 19)));
        Assert.Equal([], await Filter(FilterOperator.Gt, new GeoPointValue(10, 20)));
    }

    // ── Ne (rule 3) ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Ne_MatchesEverythingExceptEqualNullNaNMissing()
    {
        await SeedZoo();
        var ids = await Filter(FilterOperator.Ne, new IntegerValue(5));
        Assert.DoesNotContain("int5", ids);
        Assert.DoesNotContain("dbl5", ids);   // 5.0 == 5
        Assert.DoesNotContain("null", ids);
        Assert.DoesNotContain("nan", ids);
        Assert.DoesNotContain("missing", ids);
        Assert.Contains("strA", ids);          // cross-type values DO match Ne
        Assert.Contains("dbl7", ids);
        Assert.Contains("arr", ids);
    }

    // ── In / NotIn (rule 4) ──────────────────────────────────────────────────

    [Fact]
    public async Task In_TypedOrOfEqualities()
    {
        await SeedZoo();
        var ids = await Filter(FilterOperator.In,
            new ArrayValue([new DoubleValue(5.0), new StringValue("banana")]));
        Assert.Equal(["dbl5", "int5", "strB"], ids.Order());
    }

    [Fact]
    public async Task NotIn_ExcludesNullNaNMissing()
    {
        await SeedZoo();
        var ids = await Filter(FilterOperator.NotIn, new ArrayValue([new IntegerValue(5)]));
        Assert.DoesNotContain("int5", ids);
        Assert.DoesNotContain("dbl5", ids);
        Assert.DoesNotContain("null", ids);
        Assert.DoesNotContain("nan", ids);
        Assert.DoesNotContain("missing", ids);
        Assert.Contains("dbl7", ids);
    }

    // ── Array operators (rules 5, 10) ────────────────────────────────────────

    [Fact]
    public async Task ArrayContains_TypedElementEquality()
    {
        await SeedZoo();
        Assert.Equal(["arr"], await Filter(FilterOperator.ArrayContains, new DoubleValue(1.0)));  // int 1 stored
        Assert.Equal(["arr"], await Filter(FilterOperator.ArrayContains, new StringValue("x")));
        Assert.Equal([], await Filter(FilterOperator.ArrayContains, new StringValue("y")));
    }

    [Fact]
    public async Task ArrayContainsAny_And_All()
    {
        await Seed("a1", new ArrayValue([new IntegerValue(1), new IntegerValue(2)]));
        await Seed("a2", new ArrayValue([new IntegerValue(2), new IntegerValue(3)]));

        Assert.Equal(["a1", "a2"], (await Filter(FilterOperator.ArrayContainsAny,
            new ArrayValue([new IntegerValue(2)]))).Order());
        Assert.Equal(["a1"], await Filter(FilterOperator.ArrayContainsAll,
            new ArrayValue([new DoubleValue(1.0), new IntegerValue(2)])));
    }

    // ── String extensions (rule 10) ──────────────────────────────────────────

    [Fact]
    public async Task StringOps_CaseInsensitive_AndTypeGuarded()
    {
        await SeedZoo();
        Assert.Equal(["strA"], await Filter(FilterOperator.StartsWith, new StringValue("app")));   // ILIKE
        Assert.Equal(["strB"], await Filter(FilterOperator.EndsWith, new StringValue("ANA")));
        Assert.Equal(["strA", "strB"], (await Filter(FilterOperator.Contains, new StringValue("a"))).Order());
        Assert.Equal(["strB"], await Filter(FilterOperator.Regex, new StringValue("^ban.*a$")));
    }

    [Fact]
    public async Task Contains_LikeMetacharactersAreLiteral()
    {
        await Seed("pct", new StringValue("100% sure"));
        await Seed("plain", new StringValue("100 sure"));
        Assert.Equal(["pct"], await Filter(FilterOperator.Contains, new StringValue("100%")));
    }

    // ── Unary + composites + compare ─────────────────────────────────────────

    [Fact]
    public async Task Unary_IsNullIsNanExists()
    {
        await SeedZoo();
        Assert.Equal(["null"], await Ids(new QueryAst("c", Where: new UnaryFilterAst(F("f"), UnaryOp.IsNull))));
        Assert.Equal(["nan"], await Ids(new QueryAst("c", Where: new UnaryFilterAst(F("f"), UnaryOp.IsNan))));
        var exists = await Ids(new QueryAst("c", Where: new UnaryFilterAst(F("f"), UnaryOp.Exists)));
        Assert.DoesNotContain("missing", exists);
        Assert.Contains("null", exists);       // explicit null EXISTS
    }

    [Fact]
    public async Task Composite_AndOrNot()
    {
        await SeedZoo();
        var ids = await Ids(new QueryAst("c", Where: new CompositeFilterAst(CompositeOp.And,
        [
            new CompositeFilterAst(CompositeOp.Or,
            [
                new FieldFilterAst(F("f"), FilterOperator.Eq, new IntegerValue(5)),
                new FieldFilterAst(F("f"), FilterOperator.Eq, new StringValue("Apple")),
            ]),
            new CompositeFilterAst(CompositeOp.Not,
                [new FieldFilterAst(F("f"), FilterOperator.Eq, new DoubleValue(5.0))]),
        ])));
        Assert.Equal(["strA"], ids);
    }

    [Fact]
    public async Task FieldCompare_CrossFieldTypedComparison()
    {
        await SeedDoc("w1", new Dictionary<string, Value> { ["a"] = new IntegerValue(2), ["b"] = new DoubleValue(1.5) });
        await SeedDoc("w2", new Dictionary<string, Value> { ["a"] = new IntegerValue(1), ["b"] = new DoubleValue(1.5) });
        await SeedDoc("w3", new Dictionary<string, Value> { ["a"] = new IntegerValue(1) });   // b missing → no match

        Assert.Equal(["w1"], await Ids(new QueryAst("c", Where: new FieldCompareAst(F("a"), FilterOperator.Gt, F("b")))));
        Assert.Equal(["w2"], await Ids(new QueryAst("c", Where: new FieldCompareAst(F("a"), FilterOperator.Lt, F("b")))));
    }

    [Fact]
    public async Task Eq_Double_FullSeventeenDigitPrecision()
    {
        // 0.1 + 0.2 == 0.30000000000000004 — 17 significant digits; float8-param binding
        // would collapse to 0.3 and break both assertions.
        var precise = 0.1 + 0.2;
        await Seed("p", new DoubleValue(precise));
        await Seed("q", new DoubleValue(0.3));

        Assert.Equal(["p"], await Filter(FilterOperator.Eq, new DoubleValue(precise)));
        Assert.Equal(["q"], await Filter(FilterOperator.Lt, new DoubleValue(precise)));
        Assert.Equal([], await Filter(FilterOperator.Gt, new DoubleValue(precise)));
    }

    [Fact]
    public async Task Ne_Null_ExcludesNaNMissingAndNull()
    {
        await Seed("v", new IntegerValue(1));
        await Seed("nan", new DoubleValue(double.NaN));
        await Seed("nul", new NullValue());
        await SeedDoc("missing", new Dictionary<string, Value> { ["other"] = new IntegerValue(1) });

        Assert.Equal(["v"], await Filter(FilterOperator.Ne, new NullValue()));
    }

    // ── I2: Ne on __name__ ───────────────────────────────────────────────────

    [Fact]
    public async Task Ne_OnName_Works()
    {
        await Seed("a", new IntegerValue(1));
        await Seed("b", new IntegerValue(2));
        Assert.Equal(["b"], await Ids(new QueryAst("c",
            Where: new FieldFilterAst(F("__name__"), FilterOperator.Ne, new StringValue("c/a")))));
    }
}
