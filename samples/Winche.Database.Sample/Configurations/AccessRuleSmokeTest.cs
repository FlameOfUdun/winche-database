using Winche.Database.AspNetCore.Abstraction;
using Winche.Database.Interfaces;
using Winche.Database.Querying.Ast;
using Winche.Database.Values;

namespace Winche.Database.Sample.Configurations;

public static class AccessRuleSmokeTest
{
    public static async Task RunAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IDocumentManager>();
        var claimsAccessor = scope.ServiceProvider.GetRequiredService<DocumentClaimsAccessor>();

        Banner("ACCESS RULE SMOKE TESTS");

        await CleanSlateAsync(manager);
        await ScenarioQueryFiltersPerDocument(manager, claimsAccessor);

        await CleanSlateAsync(manager);
        await ScenarioQueryNoMatchingRuleDenies(manager, claimsAccessor);

        await CleanSlateAsync(manager);
        await ScenarioAggregationCollectionLevelOnly(manager, claimsAccessor);

        await CleanSlateAsync(manager);
        Banner("DONE");
    }

    // -------------------------------------------------------------------------
    // Scenarios
    // -------------------------------------------------------------------------

    private static async Task ScenarioQueryFiltersPerDocument(
        IDocumentManager manager, DocumentClaimsAccessor claimsAccessor)
    {
        Banner("Scenario 1: QueryAsync filters results per-document access rule");

        // Seed three users
        await manager.SetUnprotectedAsync("smoke-users/alice", Fields(("name", new StringValue("Alice"))));
        await manager.SetUnprotectedAsync("smoke-users/bob",   Fields(("name", new StringValue("Bob"))));
        await manager.SetUnprotectedAsync("smoke-users/charlie", Fields(("name", new StringValue("Charlie"))));

        // OwnerReadRule: smoke-users/{userId} allows Read only when uid claim == userId.
        // AllowPublicAccessRule (**) also matches and returns true, so both rules must pass.
        // Result: only smoke-users/alice is accessible when uid = "alice".

        claimsAccessor.SetClaims(new Dictionary<string, object?> { ["uid"] = "alice" });
        Console.WriteLine("Caller: uid = \"alice\"");

        var query = new QueryAst("smoke-users", Limit: 10);

        var protectedResult   = await manager.QueryAsync(query);
        var unprotectedResult = await manager.QueryUnprotectedAsync(query);

        Console.WriteLine($"  QueryAsync (protected):           {protectedResult.Documents.Count} doc(s)");
        foreach (var doc in protectedResult.Documents)
            Console.WriteLine($"    -> {doc.Path}");

        Console.WriteLine($"  QueryUnprotectedAsync (no rules): {unprotectedResult.Documents.Count} doc(s)");
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
        IDocumentManager manager, DocumentClaimsAccessor claimsAccessor)
    {
        Banner("Scenario 2: QueryAsync silently drops documents denied by rules");

        await manager.SetUnprotectedAsync("smoke-users/alice", Fields(("name", new StringValue("Alice"))));
        await manager.SetUnprotectedAsync("smoke-users/bob",   Fields(("name", new StringValue("Bob"))));

        // uid = "bob" — alice is denied, bob is allowed.
        claimsAccessor.SetClaims(new Dictionary<string, object?> { ["uid"] = "bob" });
        Console.WriteLine("Caller: uid = \"bob\"");

        var query = new QueryAst("smoke-users", Limit: 10);
        var result = await manager.QueryAsync(query);

        Console.WriteLine($"  QueryAsync result: {result.Documents.Count} doc(s)");
        foreach (var doc in result.Documents)
            Console.WriteLine($"    -> {doc.Path}");

        Assert("only bob's document is returned", result.Documents.Count == 1 &&
            result.Documents[0].Path == "smoke-users/bob");

        Assert("alice is not in the result",
            result.Documents.All(d => d.Path != "smoke-users/alice"));

        claimsAccessor.SetClaims(new Dictionary<string, object?>());
    }

    private static async Task ScenarioAggregationCollectionLevelOnly(
        IDocumentManager manager, DocumentClaimsAccessor claimsAccessor)
    {
        Banner("Scenario 3: AggregateAsync enforces collection-level access, not per-row");

        await manager.SetUnprotectedAsync("smoke-users/alice", Fields(("name", new StringValue("Alice"))));
        await manager.SetUnprotectedAsync("smoke-users/bob",   Fields(("name", new StringValue("Bob"))));
        await manager.SetUnprotectedAsync("smoke-users/charlie", Fields(("name", new StringValue("Charlie"))));

        // uid = "alice" — OwnerReadRule would deny bob and charlie at document level.
        // But AggregateAsync only checks the collection path "smoke-users", which
        // matches AllowPublicAccessRule (**). OwnerReadRule (smoke-users/{userId})
        // does NOT match the collection path "smoke-users" (no userId segment), so
        // it doesn't participate in the collection-level check.
        // Result: all 3 rows are returned — aggregation is not per-row filtered.

        claimsAccessor.SetClaims(new Dictionary<string, object?> { ["uid"] = "alice" });
        Console.WriteLine("Caller: uid = \"alice\" (OwnerReadRule would restrict per-document reads)");
        Console.WriteLine("  Collection-level check: 'smoke-users' matches AllowPublicAccessRule -> allowed");

        var pipeline = new PipelineAst([new MatchStageAst("smoke-users", null)]);

        var result = await manager.AggregateAsync(pipeline);

        Console.WriteLine($"  AggregateAsync result: {result.Rows.Count} row(s)");

        Assert("all 3 rows returned (no per-row filtering in aggregation)", result.Rows.Count == 3);

        // Contrast: QueryAsync on the same data returns only alice's doc.
        var query = new QueryAst("smoke-users", Limit: 10);
        var queryResult = await manager.QueryAsync(query);

        Console.WriteLine($"  QueryAsync on same data:          {queryResult.Documents.Count} doc(s)");

        Assert("QueryAsync still filters to 1 doc for the same caller",
            queryResult.Documents.Count == 1);

        Assert("aggregation returns more rows than filtered query",
            result.Rows.Count > queryResult.Documents.Count);

        claimsAccessor.SetClaims(new Dictionary<string, object?>());
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static async Task CleanSlateAsync(IDocumentManager manager)
    {
        await manager.DeleteUnprotectedAsync("smoke-users");
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
