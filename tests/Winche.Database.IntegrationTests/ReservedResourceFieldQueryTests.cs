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
/// Closes the reserved-name leak: query execution resolves <c>id</c>, <c>path</c>,
/// <c>collection</c>, <c>createdAt</c>, <c>updatedAt</c>, and <c>version</c> against the
/// user-writable data column, while a rule's <c>resource.&lt;name&gt;</c> resolves the SAME name to
/// a trusted storage column (see <see cref="DocumentToResource"/> / <see cref="ReservedResourceFields"/>).
/// A list rule that depends on one of those names could previously be "proved" by a query filter
/// that only matched the attacker-controlled data field, not the real metadata — <see cref="QueryToConstraints"/>
/// now skips such filters, so the query is unproven and denied instead of leaking the document.
/// </summary>
[Collection("postgres")]
public class ReservedResourceFieldQueryTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private DocumentDatabase Core() =>
        new(Fx.DataSource, Options.Create(new WincheDatabaseOptions()));

    private static RuleGuardedDocumentDatabase Guard(
        DocumentDatabase core,
        RuleSet ruleset,
        IReadOnlyDictionary<string, object?>? claims) =>
        new(core, new RuleEngine(new StaticRuleSetRepository(ruleset), WincheRuleValueComparer.Instance), new StaticClaimsAccessor(claims), Options.Create(new WincheDatabaseOptions()));

    private static Dictionary<string, Value> Fields(params (string K, Value V)[] entries) =>
        entries.ToDictionary(e => e.K, e => e.V);

    // ── The leak, closed ───────────────────────────────────────────────────────

    /// <summary>
    /// Rule: allow list if resource.id == "public". The document's REAL id is "leaked" — but its
    /// stored data forges a field named "id" set to "public", which is what a query filter on
    /// "id" actually matches (query execution resolves "id" against the data column, not the id
    /// column). Before the fix this filter "proved" the rule and leaked the document; now the
    /// filter is skipped as a constraint, the query is unproven, and the list is denied.
    /// </summary>
    [Fact]
    public async Task Query_ReservedIdFilter_ForgedDataDoesNotLeakDocument()
    {
        var core = Core();
        var ruleset = RuleSetBuilder.Build(r =>
            r.Match("secrets/{docId}", b =>
                b.Allow(RuleOperations.Read, Expr.Resource("id").Eq("public"))));

        // Real id is "leaked" (does not satisfy the rule); stored data forges id = "public".
        await core.WriteAsync([new SetWrite
        {
            Path = "secrets/leaked",
            Fields = Fields(("id", new StringValue("public")), ("payload", new StringValue("top-secret"))),
        }]);

        var guard = Guard(core, ruleset, new Dictionary<string, object?> { ["uid"] = "anyone" });

        var query = new Query(
            "secrets",
            Where: new FieldFilter(FieldPath.Parse("id"), FilterOperator.Eq, new StringValue("public")));

        await Assert.ThrowsAsync<AccessDeniedException>(() => guard.QueryAsync(query));
    }

    /// <summary>
    /// Same leak, via "collection": rule keys off resource.collection (the real storage column),
    /// a forged data field named "collection" matches the query filter but not the rule.
    /// </summary>
    [Fact]
    public async Task Query_ReservedCollectionFilter_ForgedDataDoesNotLeakDocument()
    {
        var core = Core();
        var ruleset = RuleSetBuilder.Build(r =>
            r.Match("vault/{docId}", b =>
                b.Allow(RuleOperations.Read, Expr.Resource("collection").Eq("public-collection"))));

        // Real collection is "vault"; stored data forges collection = "public-collection".
        await core.WriteAsync([new SetWrite
        {
            Path = "vault/leaked2",
            Fields = Fields(("collection", new StringValue("public-collection")), ("payload", new StringValue("top-secret"))),
        }]);

        var guard = Guard(core, ruleset, new Dictionary<string, object?> { ["uid"] = "anyone" });

        var query = new Query(
            "vault",
            Where: new FieldFilter(FieldPath.Parse("collection"), FilterOperator.Eq, new StringValue("public-collection")));

        await Assert.ThrowsAsync<AccessDeniedException>(() => guard.QueryAsync(query));
    }

    /// <summary>
    /// Same leak, via "version": rule keys off resource.version (the real storage column, which
    /// starts at 1 for a freshly-created document); a forged data field named "version" matches
    /// the query filter but the document's real version does not.
    /// </summary>
    [Fact]
    public async Task Query_ReservedVersionFilter_ForgedDataDoesNotLeakDocument()
    {
        var core = Core();
        var ruleset = RuleSetBuilder.Build(r =>
            r.Match("ledger/{docId}", b =>
                b.Allow(RuleOperations.Read, Expr.Resource("version").Eq(99L))));

        // Real version is 1 (freshly created); stored data forges version = 99.
        await core.WriteAsync([new SetWrite
        {
            Path = "ledger/leaked3",
            Fields = Fields(("version", new IntegerValue(99L)), ("payload", new StringValue("top-secret"))),
        }]);

        var guard = Guard(core, ruleset, new Dictionary<string, object?> { ["uid"] = "anyone" });

        var query = new Query(
            "ledger",
            Where: new FieldFilter(FieldPath.Parse("version"), FilterOperator.Eq, new IntegerValue(99L)));

        await Assert.ThrowsAsync<AccessDeniedException>(() => guard.QueryAsync(query));
    }

    // ── Control: an ordinary field still proves a list rule ──────────────────

    /// <summary>
    /// A filter on a non-reserved field still contributes to proving a list rule — the fix must
    /// not regress ordinary "rules are not filters"-compliant list authorization.
    /// </summary>
    [Fact]
    public async Task Query_OrdinaryFieldFilter_StillProvesRule_ReturnsDocument()
    {
        var core = Core();
        var ruleset = RuleSetBuilder.Build(r =>
            r.Match("notes/{docId}", b =>
                b.Allow(RuleOperations.Read, Expr.Resource("ownerId").Eq("alice"))));

        await core.WriteAsync([new SetWrite
        {
            Path = "notes/n1",
            Fields = Fields(("ownerId", new StringValue("alice")), ("text", new StringValue("hi"))),
        }]);

        var guard = Guard(core, ruleset, new Dictionary<string, object?> { ["uid"] = "alice" });

        var query = new Query(
            "notes",
            Where: new FieldFilter(FieldPath.Parse("ownerId"), FilterOperator.Eq, new StringValue("alice")));

        var result = await guard.QueryAsync(query);
        var doc = Assert.Single(result.Documents);
        Assert.Equal("n1", doc.Id);
    }

    // ── __name__ still works ──────────────────────────────────────────────────

    /// <summary>
    /// "__name__" is the one reserved name query execution ALSO resolves to a storage column
    /// (document_path) — see <see cref="ReservedResourceFields"/>'s remarks — so it is not in the
    /// skip set and a filter on it still proves a list rule.
    /// </summary>
    [Fact]
    public async Task Query_NameFilter_StillProvesRule_ReturnsDocument()
    {
        var core = Core();
        var ruleset = RuleSetBuilder.Build(r =>
            r.Match("pages/{docId}", b =>
                b.Allow(RuleOperations.Read, Expr.Resource("__name__").Eq(Expr.Value(RuleValue.Path("pages/p1"))))));

        await core.WriteAsync([new SetWrite
        {
            Path = "pages/p1",
            Fields = Fields(("title", new StringValue("Home"))),
        }]);

        var guard = Guard(core, ruleset, new Dictionary<string, object?> { ["uid"] = "anyone" });

        var query = new Query(
            "pages",
            Where: new FieldFilter(FieldPath.Parse("__name__"), FilterOperator.Eq, new ReferenceValue("pages/p1")));

        var result = await guard.QueryAsync(query);
        var doc = Assert.Single(result.Documents);
        Assert.Equal("p1", doc.Id);
    }
}
