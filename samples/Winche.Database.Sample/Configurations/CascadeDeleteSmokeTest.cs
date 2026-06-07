using Microsoft.Extensions.DependencyInjection;
using Winche.Database.Documents;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.Sample.Configurations;

public static class CascadeDeleteSmokeTest
{
    public static async Task RunAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        // Use the unprotected core for full CRUD without rule checks
        var core = scope.ServiceProvider.GetRequiredService<DocumentDatabase>();

        Banner("CASCADE DELETE SMOKE TEST");

        await CleanSlateAsync(core);
        await ScenarioDocumentWithSubcollection(core);

        await CleanSlateAsync(core);
        await ScenarioCollectionWithNestedDocs(core);

        await CleanSlateAsync(core);
        Banner("DONE");
    }

    private static async Task CleanSlateAsync(DocumentDatabase core)
    {
        foreach (var collection in new[] { "users", "orgs" })
            await core.WriteAsync([new DeleteWrite { Path = collection, Cascade = true }]);
    }

    private static async Task ScenarioDocumentWithSubcollection(DocumentDatabase core)
    {
        Banner("Scenario 1: delete a document that has sub-collections");

        await core.WriteAsync([new SetWrite { Path = "users/alice", Fields = Fields(("name", new StringValue("Alice"))) }]);
        await core.WriteAsync([new SetWrite { Path = "users/alice/posts/p1", Fields = Fields(("title", new StringValue("Hello"))) }]);
        await core.WriteAsync([new SetWrite { Path = "users/alice/posts/p2", Fields = Fields(("title", new StringValue("World"))) }]);
        await core.WriteAsync([new SetWrite { Path = "users/alice/posts/p1/likes/l1", Fields = Fields(("by", new StringValue("bob"))) }]);
        await core.WriteAsync([new SetWrite { Path = "users/bob", Fields = Fields(("name", new StringValue("Bob"))) }]);

        Console.WriteLine("Seeded:");
        await PrintExistence(core, [
            "users/alice",
            "users/alice/posts/p1",
            "users/alice/posts/p2",
            "users/alice/posts/p1/likes/l1",
            "users/bob",
        ]);

        Console.WriteLine();
        Console.WriteLine("Calling WriteAsync(DeleteWrite \"users/alice\", Cascade=true)...");
        await core.WriteAsync([new DeleteWrite { Path = "users/alice", Cascade = true }]);
        Console.WriteLine();

        Console.WriteLine("After delete:");
        await PrintExistence(core, [
            "users/alice",
            "users/alice/posts/p1",
            "users/alice/posts/p2",
            "users/alice/posts/p1/likes/l1",
            "users/bob",
        ]);

        Assert("users/alice gone",                 await core.GetAsync("users/alice")                  is null);
        Assert("users/alice/posts/p1 gone",        await core.GetAsync("users/alice/posts/p1")         is null);
        Assert("users/alice/posts/p2 gone",        await core.GetAsync("users/alice/posts/p2")         is null);
        Assert("users/alice/posts/p1/likes/l1 gone", await core.GetAsync("users/alice/posts/p1/likes/l1") is null);
        Assert("users/bob preserved",              await core.GetAsync("users/bob")                    is not null);
    }

    private static async Task ScenarioCollectionWithNestedDocs(DocumentDatabase core)
    {
        Banner("Scenario 2: delete a collection that has documents with sub-collections");

        await core.WriteAsync([new SetWrite { Path = "orgs/acme", Fields = Fields(("name", new StringValue("ACME"))) }]);
        await core.WriteAsync([new SetWrite { Path = "orgs/acme/teams/t1", Fields = Fields(("name", new StringValue("Team 1"))) }]);
        await core.WriteAsync([new SetWrite { Path = "orgs/acme/teams/t1/members/m1", Fields = Fields(("name", new StringValue("Member 1"))) }]);
        await core.WriteAsync([new SetWrite { Path = "orgs/acme/teams/t1/members/m2", Fields = Fields(("name", new StringValue("Member 2"))) }]);
        await core.WriteAsync([new SetWrite { Path = "orgs/acme/teams/t2", Fields = Fields(("name", new StringValue("Team 2"))) }]);
        await core.WriteAsync([new SetWrite { Path = "orgs/acme/teams/t2/members/m3", Fields = Fields(("name", new StringValue("Member 3"))) }]);
        await core.WriteAsync([new SetWrite { Path = "orgs/acme/projects/pr1", Fields = Fields(("name", new StringValue("Project 1"))) }]);
        await core.WriteAsync([new SetWrite { Path = "orgs/other", Fields = Fields(("name", new StringValue("Other Org"))) }]);

        Console.WriteLine("Seeded:");
        await PrintExistence(core, [
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
        Console.WriteLine("Calling WriteAsync(DeleteWrite \"orgs/acme/teams\", Cascade=true) -- collection path (even slash count)");
        await core.WriteAsync([new DeleteWrite { Path = "orgs/acme/teams", Cascade = true }]);
        Console.WriteLine();

        Console.WriteLine("After delete:");
        await PrintExistence(core, [
            "orgs/acme",
            "orgs/acme/teams/t1",
            "orgs/acme/teams/t1/members/m1",
            "orgs/acme/teams/t1/members/m2",
            "orgs/acme/teams/t2",
            "orgs/acme/teams/t2/members/m3",
            "orgs/acme/projects/pr1",
            "orgs/other",
        ]);

        Assert("orgs/acme preserved",                       await core.GetAsync("orgs/acme")                       is not null);
        Assert("orgs/acme/teams/t1 gone",                   await core.GetAsync("orgs/acme/teams/t1")              is null);
        Assert("orgs/acme/teams/t1/members/m1 gone",        await core.GetAsync("orgs/acme/teams/t1/members/m1")   is null);
        Assert("orgs/acme/teams/t1/members/m2 gone",        await core.GetAsync("orgs/acme/teams/t1/members/m2")   is null);
        Assert("orgs/acme/teams/t2 gone",                   await core.GetAsync("orgs/acme/teams/t2")              is null);
        Assert("orgs/acme/teams/t2/members/m3 gone",        await core.GetAsync("orgs/acme/teams/t2/members/m3")   is null);
        Assert("orgs/acme/projects/pr1 preserved (sibling)", await core.GetAsync("orgs/acme/projects/pr1")         is not null);
        Assert("orgs/other preserved",                      await core.GetAsync("orgs/other")                     is not null);
    }

    private static async Task PrintExistence(DocumentDatabase core, IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            var doc = await core.GetAsync(p);
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
