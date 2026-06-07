using Winche.Database.Interfaces;
using Winche.Database.Values;

namespace Winche.Database.Sample.Configurations;

public static class CascadeDeleteSmokeTest
{
    public static async Task RunAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IDocumentManager>();

        Banner("CASCADE DELETE SMOKE TEST");

        await CleanSlateAsync(manager);
        await ScenarioDocumentWithSubcollection(manager);

        await CleanSlateAsync(manager);
        await ScenarioCollectionWithNestedDocs(manager);

        await CleanSlateAsync(manager);
        Banner("DONE");
    }

    private static async Task CleanSlateAsync(IDocumentManager manager)
    {
        // Top-level collections used by the scenarios.
        foreach (var collection in new[] { "users", "orgs" })
            await manager.DeleteUnprotectedAsync(collection);
    }

    private static async Task ScenarioDocumentWithSubcollection(IDocumentManager manager)
    {
        Banner("Scenario 1: delete a document that has sub-collections");

        await manager.SetUnprotectedAsync("users/alice", Fields(("name", new StringValue("Alice"))));
        await manager.SetUnprotectedAsync("users/alice/posts/p1", Fields(("title", new StringValue("Hello"))));
        await manager.SetUnprotectedAsync("users/alice/posts/p2", Fields(("title", new StringValue("World"))));
        await manager.SetUnprotectedAsync("users/alice/posts/p1/likes/l1", Fields(("by", new StringValue("bob"))));
        await manager.SetUnprotectedAsync("users/bob", Fields(("name", new StringValue("Bob"))));

        Console.WriteLine("Seeded:");
        await PrintExistence(manager, [
            "users/alice",
            "users/alice/posts/p1",
            "users/alice/posts/p2",
            "users/alice/posts/p1/likes/l1",
            "users/bob",
        ]);

        Console.WriteLine();
        Console.WriteLine("Calling DeleteAsync(\"users/alice\")...");
        var ok = await manager.DeleteAsync("users/alice");
        Console.WriteLine($"  returned: {ok}");
        Console.WriteLine();

        Console.WriteLine("After delete:");
        await PrintExistence(manager, [
            "users/alice",
            "users/alice/posts/p1",
            "users/alice/posts/p2",
            "users/alice/posts/p1/likes/l1",
            "users/bob",
        ]);

        Assert("users/alice gone",                 await manager.GetUnprotectedAsync("users/alice")                  is null);
        Assert("users/alice/posts/p1 gone",        await manager.GetUnprotectedAsync("users/alice/posts/p1")         is null);
        Assert("users/alice/posts/p2 gone",        await manager.GetUnprotectedAsync("users/alice/posts/p2")         is null);
        Assert("users/alice/posts/p1/likes/l1 gone", await manager.GetUnprotectedAsync("users/alice/posts/p1/likes/l1") is null);
        Assert("users/bob preserved",              await manager.GetUnprotectedAsync("users/bob")                    is not null);
    }

    private static async Task ScenarioCollectionWithNestedDocs(IDocumentManager manager)
    {
        Banner("Scenario 2: delete a collection that has documents with sub-collections");

        await manager.SetUnprotectedAsync("orgs/acme", Fields(("name", new StringValue("ACME"))));
        await manager.SetUnprotectedAsync("orgs/acme/teams/t1", Fields(("name", new StringValue("Team 1"))));
        await manager.SetUnprotectedAsync("orgs/acme/teams/t1/members/m1", Fields(("name", new StringValue("Member 1"))));
        await manager.SetUnprotectedAsync("orgs/acme/teams/t1/members/m2", Fields(("name", new StringValue("Member 2"))));
        await manager.SetUnprotectedAsync("orgs/acme/teams/t2", Fields(("name", new StringValue("Team 2"))));
        await manager.SetUnprotectedAsync("orgs/acme/teams/t2/members/m3", Fields(("name", new StringValue("Member 3"))));
        await manager.SetUnprotectedAsync("orgs/acme/projects/pr1", Fields(("name", new StringValue("Project 1"))));
        await manager.SetUnprotectedAsync("orgs/other", Fields(("name", new StringValue("Other Org"))));

        Console.WriteLine("Seeded:");
        await PrintExistence(manager, [
            "orgs/acme",
            "orgs/acme/teams/t1",
            "orgs/acme/teams/t1/members/m1",
            "orgs/acme/teams/t1/members/m2",
            "orgs/acme/teams/t2",
            "orgs/acme/teams/t2/members/m3",
            "orgs/acme/projects/pr1",
            "orgs/other",
        ]);

        Console.WriteLine();
        Console.WriteLine("Calling DeleteAsync(\"orgs/acme/teams\")  -- collection path (even slash count)");
        var ok = await manager.DeleteAsync("orgs/acme/teams");
        Console.WriteLine($"  returned: {ok}");
        Console.WriteLine();

        Console.WriteLine("After delete:");
        await PrintExistence(manager, [
            "orgs/acme",
            "orgs/acme/teams/t1",
            "orgs/acme/teams/t1/members/m1",
            "orgs/acme/teams/t1/members/m2",
            "orgs/acme/teams/t2",
            "orgs/acme/teams/t2/members/m3",
            "orgs/acme/projects/pr1",
            "orgs/other",
        ]);

        Assert("orgs/acme preserved",                       await manager.GetUnprotectedAsync("orgs/acme")                       is not null);
        Assert("orgs/acme/teams/t1 gone",                   await manager.GetUnprotectedAsync("orgs/acme/teams/t1")              is null);
        Assert("orgs/acme/teams/t1/members/m1 gone",        await manager.GetUnprotectedAsync("orgs/acme/teams/t1/members/m1")   is null);
        Assert("orgs/acme/teams/t1/members/m2 gone",        await manager.GetUnprotectedAsync("orgs/acme/teams/t1/members/m2")   is null);
        Assert("orgs/acme/teams/t2 gone",                   await manager.GetUnprotectedAsync("orgs/acme/teams/t2")              is null);
        Assert("orgs/acme/teams/t2/members/m3 gone",        await manager.GetUnprotectedAsync("orgs/acme/teams/t2/members/m3")   is null);
        Assert("orgs/acme/projects/pr1 preserved (sibling)", await manager.GetUnprotectedAsync("orgs/acme/projects/pr1")         is not null);
        Assert("orgs/other preserved",                      await manager.GetUnprotectedAsync("orgs/other")                     is not null);
    }

    private static async Task PrintExistence(IDocumentManager manager, IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            var doc = await manager.GetUnprotectedAsync(p);
            Console.WriteLine($"  {(doc is null ? "  -  " : "EXIST")}  {p}");
        }
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
