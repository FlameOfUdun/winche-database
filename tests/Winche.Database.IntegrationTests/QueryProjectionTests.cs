using Microsoft.Extensions.Options;
using Winche.Database.Authorization;
using Winche.Database.DependencyInjection;
using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;
using Winche.Rules;
using Winche.Rules.Expressions;

namespace Winche.Database.IntegrationTests;

/// <summary>
/// Integration tests for Query.Select (field projection).
/// Verifies that only selected fields are returned, nested paths are reconstructed,
/// absent paths are silently omitted, no-select still returns all fields,
/// and that authorization is orthogonal to the projection.
/// </summary>
[Collection("postgres")]
public class QueryProjectionTests(PostgresFixture fx) : QueryTestBase(fx)
{
    // ── 1. All fields returned when Select is absent (regression) ─────────────

    [Fact]
    public async Task NoSelect_AllFieldsReturned()
    {
        await SeedDoc("u1", new Dictionary<string, Value>
        {
            ["name"]  = new StringValue("Alice"),
            ["age"]   = new IntegerValue(30),
            ["email"] = new StringValue("alice@example.com"),
        });

        var result = await Run(new Query("c"));

        var doc = Assert.Single(result.Documents);
        Assert.Equal(3, doc.Fields.Count);
        Assert.True(doc.Fields.ContainsKey("name"));
        Assert.True(doc.Fields.ContainsKey("age"));
        Assert.True(doc.Fields.ContainsKey("email"));
    }

    // ── 2. Select single top-level field ──────────────────────────────────────

    [Fact]
    public async Task SelectOneField_OnlyThatFieldPresent_IdPathPreserved()
    {
        await SeedDoc("u1", new Dictionary<string, Value>
        {
            ["name"]  = new StringValue("Bob"),
            ["score"] = new IntegerValue(42),
            ["admin"] = new BooleanValue(false),
        });

        var result = await Run(new Query("c", Select: [F("name")]));

        var doc = Assert.Single(result.Documents);
        Assert.Equal("u1",  doc.Id);
        Assert.Equal("c/u1", doc.Path);
        Assert.Single(doc.Fields);
        Assert.Equal(new StringValue("Bob"), doc.Fields["name"]);
        Assert.False(doc.Fields.ContainsKey("score"));
        Assert.False(doc.Fields.ContainsKey("admin"));
    }

    // ── 3. Select multiple top-level fields ───────────────────────────────────

    [Fact]
    public async Task SelectMultipleFields_OnlyThosePresent()
    {
        await SeedDoc("u1", new Dictionary<string, Value>
        {
            ["a"] = new IntegerValue(1),
            ["b"] = new IntegerValue(2),
            ["c"] = new IntegerValue(3),
            ["d"] = new IntegerValue(4),
        });

        var result = await Run(new Query("c", Select: [F("a"), F("c")]));

        var doc = Assert.Single(result.Documents);
        Assert.Equal(2, doc.Fields.Count);
        Assert.Equal(new IntegerValue(1), doc.Fields["a"]);
        Assert.Equal(new IntegerValue(3), doc.Fields["c"]);
    }

    // ── 4. Nested path select ("address.city") ────────────────────────────────

    [Fact]
    public async Task NestedPathSelect_ReconstructsOnlySelectedBranch()
    {
        await SeedDoc("u1", new Dictionary<string, Value>
        {
            ["name"] = new StringValue("Carol"),
            ["address"] = new MapValue(new Dictionary<string, Value>
            {
                ["city"]    = new StringValue("Oslo"),
                ["country"] = new StringValue("Norway"),
                ["zip"]     = new StringValue("0001"),
            }),
        });

        var result = await Run(new Query("c", Select: [F("address.city")]));

        var doc = Assert.Single(result.Documents);
        Assert.Equal("u1", doc.Id);
        // Only "address" key present at top level
        Assert.Single(doc.Fields);
        var addrMap = Assert.IsType<MapValue>(doc.Fields["address"]);
        // Only "city" within address
        Assert.Single(addrMap.Fields);
        Assert.Equal(new StringValue("Oslo"), addrMap.Fields["city"]);
        Assert.False(addrMap.Fields.ContainsKey("country"));
        Assert.False(addrMap.Fields.ContainsKey("zip"));
    }

    // ── 5. Selected path absent in some docs → those docs just omit it ────────

    [Fact]
    public async Task AbsentPathInSomeDocs_SilentlyOmitted_NoError()
    {
        // u1 has both fields; u2 only has "score"
        await SeedDoc("u1", new Dictionary<string, Value>
        {
            ["name"]  = new StringValue("Dan"),
            ["score"] = new IntegerValue(10),
        });
        await SeedDoc("u2", new Dictionary<string, Value>
        {
            ["score"] = new IntegerValue(20),
        });

        var result = await Run(new Query("c", Select: [F("name"), F("score")]));

        Assert.Equal(2, result.Documents.Count);
        var d1 = result.Documents.Single(d => d.Id == "u1");
        var d2 = result.Documents.Single(d => d.Id == "u2");

        // u1: both fields present
        Assert.Equal(2, d1.Fields.Count);
        Assert.Equal(new StringValue("Dan"), d1.Fields["name"]);

        // u2: "name" absent — field simply not in result
        Assert.Single(d2.Fields);
        Assert.False(d2.Fields.ContainsKey("name"));
        Assert.Equal(new IntegerValue(20), d2.Fields["score"]);
    }

    // ── 6. Select across multiple docs (query returns several) ────────────────

    [Fact]
    public async Task SelectWithMultipleDocs_AllProjectedCorrectly()
    {
        for (var i = 1; i <= 3; i++)
            await SeedDoc($"u{i}", new Dictionary<string, Value>
            {
                ["name"]  = new StringValue($"User{i}"),
                ["score"] = new IntegerValue(i * 10),
                ["extra"] = new BooleanValue(true),
            });

        var result = await Run(new Query("c", Select: [F("score")]));

        Assert.Equal(3, result.Documents.Count);
        Assert.All(result.Documents, doc =>
        {
            Assert.Single(doc.Fields);
            Assert.True(doc.Fields.ContainsKey("score"));
            Assert.False(doc.Fields.ContainsKey("name"));
            Assert.False(doc.Fields.ContainsKey("extra"));
        });
    }

    // ── 7. Select combined with WHERE filter ─────────────────────────────────

    [Fact]
    public async Task SelectWithWhereFilter_FilterAppliedAndProjectionApplied()
    {
        await SeedDoc("u1", new Dictionary<string, Value>
        {
            ["active"] = new BooleanValue(true),
            ["name"]   = new StringValue("Eve"),
            ["secret"] = new StringValue("hidden"),
        });
        await SeedDoc("u2", new Dictionary<string, Value>
        {
            ["active"] = new BooleanValue(false),
            ["name"]   = new StringValue("Frank"),
            ["secret"] = new StringValue("also hidden"),
        });

        var result = await Run(new Query("c",
            Where: new FieldFilter(F("active"), FilterOperator.Eq, new BooleanValue(true)),
            Select: [F("name")]));

        var doc = Assert.Single(result.Documents);
        Assert.Equal("u1", doc.Id);
        Assert.Single(doc.Fields);
        Assert.Equal(new StringValue("Eve"), doc.Fields["name"]);
        Assert.False(doc.Fields.ContainsKey("secret"));
        Assert.False(doc.Fields.ContainsKey("active")); // not selected
    }

    // ── 8. Authorization orthogonality: owner rule + Select ──────────────────

    /// <summary>
    /// Critical: authorization is based solely on WHERE constraints.
    /// A query with WHERE ownerId == uid AND Select: ["name"] must be authorized
    /// (same as without Select) and must return docs trimmed to selected fields only.
    /// </summary>
    [Fact]
    public async Task Select_AuthorizationOrthogonal_OwnerConstrainedQueryReturnsOwnedDocsTrimmed()
    {
        var core = new DocumentDatabase(Fx.DataSource, Options.Create(new WincheDatabaseOptions()));

        var ruleset = RulesetBuilder.Build(r =>
            r.Match("owners/{docId}", b =>
                b.Allow(
                    RuleOperations.Read,
                    Expr.Resource("ownerId").Eq(Expr.Auth("uid")))));

        // Seed via core
        await core.WriteAsync([new SetWrite
        {
            Path = "owners/a1",
            Fields = new Dictionary<string, Value>
            {
                ["ownerId"] = new StringValue("alice"),
                ["name"]    = new StringValue("Alice"),
                ["secret"]  = new StringValue("top-secret"),
            },
        }]);
        await core.WriteAsync([new SetWrite
        {
            Path = "owners/b1",
            Fields = new Dictionary<string, Value>
            {
                ["ownerId"] = new StringValue("bob"),
                ["name"]    = new StringValue("Bob"),
                ["secret"]  = new StringValue("bob-secret"),
            },
        }]);

        var aliceGuard = new RuleGuardedDocumentDatabase(
            core, new RuleEngine(ruleset, WincheRuleValueComparer.Instance),
            new StaticClaimsAccessor(new Dictionary<string, object?> { ["uid"] = "alice" }));

        // Query with WHERE ownerId == "alice" AND Select: ["name"]
        // Authorization must pass (constraint satisfies the rule).
        // Result must be alice's doc, trimmed to only "name".
        var query = new Query(
            "owners",
            Where: new FieldFilter(FieldPath.Parse("ownerId"), FilterOperator.Eq, new StringValue("alice")),
            Select: [FieldPath.Parse("name")]);

        var result = await aliceGuard.QueryAsync(query);

        var doc = Assert.Single(result.Documents);
        Assert.Equal("a1", doc.Id);
        // Only "name" field — "ownerId" and "secret" must NOT be present
        Assert.Single(doc.Fields);
        Assert.Equal(new StringValue("Alice"), doc.Fields["name"]);
        Assert.False(doc.Fields.ContainsKey("ownerId"));
        Assert.False(doc.Fields.ContainsKey("secret"));
    }

    /// <summary>
    /// Authorization denial is unaffected by Select:
    /// an unconstrained query with a Select still throws AccessDeniedException.
    /// </summary>
    [Fact]
    public async Task Select_DoesNotBypassAuthorization_UnconstrainedQueryDenied()
    {
        var core = new DocumentDatabase(Fx.DataSource, Options.Create(new WincheDatabaseOptions()));

        var ruleset = RulesetBuilder.Build(r =>
            r.Match("private/{docId}", b =>
                b.Allow(
                    RuleOperations.Read,
                    Expr.Resource("ownerId").Eq(Expr.Auth("uid")))));

        await core.WriteAsync([new SetWrite
        {
            Path = "private/p1",
            Fields = new Dictionary<string, Value> { ["ownerId"] = new StringValue("alice") },
        }]);

        var aliceGuard = new RuleGuardedDocumentDatabase(
            core, new RuleEngine(ruleset, WincheRuleValueComparer.Instance),
            new StaticClaimsAccessor(new Dictionary<string, object?> { ["uid"] = "alice" }));

        // Unconstrained query + Select: ["ownerId"] → still denied (rules are not filters)
        var query = new Query("private", Select: [FieldPath.Parse("ownerId")]);

        await Assert.ThrowsAsync<AccessDeniedException>(() => aliceGuard.QueryAsync(query));
    }
}
