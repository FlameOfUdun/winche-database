using Microsoft.Extensions.DependencyInjection;
using Winche.Database.AspNetCore.Abstraction;
using Winche.Database.Querying.Ast;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;
using Winche.Sentinel.Models;

namespace Winche.Database.Sample.Configurations;

public static class AccessRuleSmokeTest
{
    public static async Task RunAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IDocumentDatabase>();
        var core = scope.ServiceProvider.GetRequiredService<DocumentDatabase>();
        var claimsAccessor = scope.ServiceProvider.GetRequiredService<DocumentClaimsAccessor>();

        Banner("ACCESS RULE SMOKE TESTS");

        await CleanSlateAsync(core);
        await ScenarioQueryFiltersPerDocument(db, core, claimsAccessor);

        await CleanSlateAsync(core);
        await ScenarioQueryNoMatchingRuleDenies(db, claimsAccessor);

        await CleanSlateAsync(core);
        await ScenarioAggregationRequiresOptIn(db, core, claimsAccessor);

        await CleanSlateAsync(core);
        Banner("DONE");
    }

    // -------------------------------------------------------------------------
    // Scenarios
    // -------------------------------------------------------------------------

    private static async Task ScenarioQueryFiltersPerDocument(
        IDocumentDatabase db, DocumentDatabase core, DocumentClaimsAccessor claimsAccessor)
    {
        Banner("Scenario 1: QueryAsync filters results per-document access rule");

        // Seed three users (unprotected)
        await core.WriteAsync([new SetWrite { Path = "smoke-users/alice", Fields = Fields(("name", new StringValue("Alice"))) }]);
        await core.WriteAsync([new SetWrite { Path = "smoke-users/bob",   Fields = Fields(("name", new StringValue("Bob"))) }]);
        await core.WriteAsync([new SetWrite { Path = "smoke-users/charlie", Fields = Fields(("name", new StringValue("Charlie"))) }]);

        // OwnerReadRule: smoke-users/{userId} allows Read only when uid claim == userId.
        // AllowPublicAccessRule (**) also matches and returns true, so both rules must pass.
        // Result: only smoke-users/alice is accessible when uid = "alice".

        claimsAccessor.SetClaims(new Dictionary<string, object?> { ["uid"] = "alice" });
        Console.WriteLine("Caller: uid = \"alice\"");

        var query = new QueryAst("smoke-users", Limit: 10);

        var protectedResult   = await db.QueryAsync(query);
        var unprotectedResult = await core.QueryAsync(query);

        Console.WriteLine($"  QueryAsync (protected):           {protectedResult.Documents.Count} doc(s)");
        foreach (var doc in protectedResult.Documents)
            Console.WriteLine($"    -> {doc.Path}");

        Console.WriteLine($"  QueryAsync (unprotected):         {unprotectedResult.Documents.Count} doc(s)");
        foreach (var doc in unprotectedResult.Documents)
            Console.WriteLine($"    -> {doc.Path}");

        Assert("protected returns only alice's document",
            protectedResult.Documents.Count == 1 &&
            protectedResult.Documents[0].Path == "smoke-users/alice");

        Assert("unprotected returns all 3 documents",
            unprotectedResult.Documents.Count == 3);

        Assert("protected returns fewer documents than unprotected",
            protectedResult.Documents.Count < unprotectedResult.Documents.Count);

        claimsAccessor.SetClaims(new Dictionary<string, object?>());
    }

    private static async Task ScenarioQueryNoMatchingRuleDenies(
        IDocumentDatabase db, DocumentClaimsAccessor claimsAccessor)
    {
        Banner("Scenario 2: QueryAsync silently drops documents denied by rules");

        // Use db (guarded) for seeding here — write rules allow in sample config
        // Actually write via WriteAsync on guarded (AllowPublicAccessRule allows writes)
        await db.WriteAsync([new SetWrite { Path = "smoke-users/alice", Fields = Fields(("name", new StringValue("Alice"))) }]);
        await db.WriteAsync([new SetWrite { Path = "smoke-users/bob",   Fields = Fields(("name", new StringValue("Bob"))) }]);

        // uid = "bob" — alice is denied, bob is allowed.
        claimsAccessor.SetClaims(new Dictionary<string, object?> { ["uid"] = "bob" });
        Console.WriteLine("Caller: uid = \"bob\"");

        var query = new QueryAst("smoke-users", Limit: 10);
        var result = await db.QueryAsync(query);

        Console.WriteLine($"  QueryAsync result: {result.Documents.Count} doc(s)");
        foreach (var doc in result.Documents)
            Console.WriteLine($"    -> {doc.Path}");

        Assert("only bob's document is returned", result.Documents.Count == 1 &&
            result.Documents[0].Path == "smoke-users/bob");

        Assert("alice is not in the result",
            result.Documents.All(d => d.Path != "smoke-users/alice"));

        claimsAccessor.SetClaims(new Dictionary<string, object?>());
    }

    private static async Task ScenarioAggregationRequiresOptIn(
        IDocumentDatabase db, DocumentDatabase core, DocumentClaimsAccessor claimsAccessor)
    {
        Banner("Scenario 3: AggregateAsync requires an explicit Aggregate opt-in (collection-level)");

        await core.WriteAsync([new SetWrite { Path = "smoke-users/alice", Fields = Fields(("name", new StringValue("Alice"))) }]);
        await core.WriteAsync([new SetWrite { Path = "smoke-users/bob",   Fields = Fields(("name", new StringValue("Bob"))) }]);
        await core.WriteAsync([new SetWrite { Path = "smoke-users/charlie", Fields = Fields(("name", new StringValue("Charlie"))) }]);

        // Aggregation is gated by AccessOperation.Aggregate, NOT Read. SmokeUsersAggregateRule grants
        // Aggregate on "smoke-users", so aggregation is allowed — and it returns ALL rows. Aggregation
        // is collection-level and never per-row filtered: per-document rules like OwnerReadRule cannot
        // protect a scalar aggregate (a sum/count already encodes rows you cannot read), which is
        // exactly why the Aggregate grant is a separate, deliberate opt-in rather than something Read
        // implies.

        claimsAccessor.SetClaims(new Dictionary<string, object?> { ["uid"] = "alice" });
        Console.WriteLine("Caller: uid = \"alice\" (OwnerReadRule would restrict per-document reads)");
        Console.WriteLine("  'smoke-users' has an Aggregate grant (SmokeUsersAggregateRule) -> allowed");

        var pipeline = new PipelineAst([new MatchStageAst("smoke-users", null)]);

        var result = await db.AggregateAsync(pipeline);

        Console.WriteLine($"  AggregateAsync result: {result.Rows.Count} row(s)");

        Assert("all 3 rows returned (aggregation is collection-level, not per-row filtered)", result.Rows.Count == 3);

        // Contrast: QueryAsync on the same data returns only alice's doc (per-document filtering).
        var query = new QueryAst("smoke-users", Limit: 10);
        var queryResult = await db.QueryAsync(query);

        Console.WriteLine($"  QueryAsync on same data:          {queryResult.Documents.Count} doc(s)");

        Assert("QueryAsync still filters to 1 doc for the same caller",
            queryResult.Documents.Count == 1);

        Assert("aggregation returns more rows than filtered query",
            result.Rows.Count > queryResult.Documents.Count);

        // Deny-by-default: a collection WITHOUT an Aggregate grant is rejected, even though
        // AllowPublicAccessRule (**) grants Read/Write/Delete to every path. Read does not imply Aggregate.
        var deniedThrown = false;
        try
        {
            await db.AggregateAsync(new PipelineAst([new MatchStageAst("smoke-secrets", null)]));
        }
        catch (NoRulesMatchedException)
        {
            deniedThrown = true;
        }
        Console.WriteLine("  Aggregating 'smoke-secrets' (no Aggregate grant) -> denied");
        Assert("aggregation over a collection without an Aggregate grant is denied", deniedThrown);

        claimsAccessor.SetClaims(new Dictionary<string, object?>());
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static async Task CleanSlateAsync(DocumentDatabase core)
    {
        await core.WriteAsync([new DeleteWrite { Path = "smoke-users", Cascade = true }]);
    }

    private static void Assert(string label, bool ok)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
        Console.ForegroundColor = prev;
    }

    private static void Banner(string title)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', Math.Max(40, title.Length + 4)));
        Console.WriteLine($"  {title}");
        Console.WriteLine(new string('=', Math.Max(40, title.Length + 4)));
    }

    private static IReadOnlyDictionary<string, Value> Fields(params (string Key, Value Value)[] entries)
    {
        var d = new Dictionary<string, Value>();
        foreach (var (k, v) in entries) d[k] = v;
        return d;
    }
}
