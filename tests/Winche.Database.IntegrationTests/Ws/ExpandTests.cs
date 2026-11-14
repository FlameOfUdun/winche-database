using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Winche.Database.Authorization;
using Winche.Database.DependencyInjection;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Expansion;
using Winche.Database.Runtime.Writes;
using Winche.Rules;
using Winche.Rules.Expressions;
using Winche.Values;

namespace Winche.Database.IntegrationTests.Ws;

/// <summary>expand: references followed on reads, queries and live subscriptions, as the caller, kept current.</summary>
[Collection("postgres")]
public class ExpandTests(PostgresFixture fx) : QueryTestBase(fx)
{
    // Everything readable and writable, except documents in a "secret…" collection, which nobody may read.
    private static readonly RuleSet Rules = RuleSetBuilder.Build(r =>
    {
        r.Match("{coll}/{id}", b => b.Allow(RuleOperations.Of(RuleOperation.Get, RuleOperation.List),
            Expr.Param("coll").In(Expr.Value(new ArrayValue([new StringValue(Secret)]))).Not()));
        r.Match("{document=**}", b => b.Allow(RuleOperations.Of(RuleOperation.Create, RuleOperation.Update, RuleOperation.Delete), Expr.Const(true)));
    });
    private const string Secret = "xsecret";

    private DocumentDatabase Core() => new(Fx.DataSource, Options.Create(new WincheDatabaseOptions()));
    private static ReferenceValue Ref(string path) => new(path);
    private static Dictionary<string, Value> F(params (string, Value)[] f) => f.ToDictionary(x => x.Item1, x => x.Item2);
    private static string U(string what) => $"{what}{Guid.NewGuid():N}"[..20];

    /// <summary>patients/p → doctor (doctors/d → clinic (clinics/c → head: back to the doctor)), team [staff a, b], address.owner, a secret and a missing one.</summary>
    private async Task<(string P, string D, string C, string A, string B, string O, string S)> SeedAsync()
    {
        var (p, d, c, a, b, o, s) = ($"{U("patients")}/p", $"{U("doctors")}/d", $"{U("clinics")}/c", $"{U("staff")}/a", $"{U("staff")}/b", $"{U("people")}/o", $"{Secret}/{U("s")}");
        await Core().WriteAsync([
            new SetWrite { Path = c, Fields = F(("name", new StringValue("North")), ("head", Ref(d))) },
            new SetWrite { Path = d, Fields = F(("name", new StringValue("Dr Who")), ("clinic", Ref(c))) },
            new SetWrite { Path = a, Fields = F(("name", new StringValue("A"))) },
            new SetWrite { Path = b, Fields = F(("name", new StringValue("B"))) },
            new SetWrite { Path = o, Fields = F(("name", new StringValue("Owner"))) },
            new SetWrite { Path = s, Fields = F(("code", new StringValue("42"))) },
            new SetWrite { Path = p, Fields = F(
                ("doctor", Ref(d)), ("team", new ArrayValue([Ref(a), Ref(b)])), ("address", new MapValue(F(("owner", Ref(o))))),
                ("secret", Ref(s)), ("gone", Ref("nowhere/x")), ("note", new StringValue("not a reference"))) },
        ]);
        return (p, d, c, a, b, o, s);
    }

    private RuleGuardedDocumentDatabase Guarded() =>
        new(Core(), new RuleEngine(new StaticRuleSetRepository(Rules)), new StaticClaimsAccessor(new Dictionary<string, object?> { ["uid"] = "ann" }));

    [Fact]
    public async Task References_are_followed_through_maps_lists_and_documents_once_each_as_the_caller()
    {
        var (p, d, c, a, b, o, s) = await SeedAsync();
        var db = Guarded();
        var expansion = await ReferenceExpander.ExpandAsync(db, [await db.GetAsync(p)],
            ExpandRequest.Of(["doctor.clinic.head", "team", "address.owner", "secret", "gone", "note"]));

        Assert.Equal(new[] { a, b, c, d, o }.Order(), expansion.Included.Keys.Order());   // the cycle back to the doctor is read once
        Assert.Contains(s, expansion.Referenced);                                          // referenced, but not readable: stays a pointer
        Assert.Contains("nowhere/x", expansion.Referenced);
        Assert.False(expansion.Truncated);
        Assert.Throws<RuntimeException>(() => ExpandRequest.Of(["a..b"]));
    }

    private Task<WsTestHost> Host() => WsTestHost.StartAsync(Fx.ConnectionString, o => o.UseRules(Rules));

    [Fact]
    public async Task Get_and_query_send_the_included_documents()
    {
        var (p, d, _, _, _, _, s) = await SeedAsync();
        await using var host = await Host();
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server, "uid:ann");

        var get = await ws.RequestAsync(new JsonObject { ["type"] = "doc.get", ["path"] = p, ["expand"] = new JsonArray("doctor", "secret") });
        var included = get["result"]!["included"]!.AsObject();
        Assert.Equal("Dr Who", (string?)included[d]!["fields"]!["name"]!["stringValue"]);
        Assert.False(included.ContainsKey(s));
        Assert.Equal(d, (string?)get["result"]!["document"]!["fields"]!["doctor"]!["docRefValue"]);   // the field itself stays a pointer

        var collection = p.Split('/')[0];
        var query = await ws.RequestAsync(new JsonObject { ["type"] = "query", ["query"] = new JsonObject { ["collection"] = collection }, ["expand"] = new JsonArray("doctor") });
        Assert.True(query["result"]!["included"]!.AsObject().ContainsKey(d));

        var plain = await ws.RequestAsync(new JsonObject { ["type"] = "doc.get", ["path"] = p });
        Assert.Null(plain["result"]!["included"]);   // only when asked
    }

    [Fact]
    public async Task A_live_subscription_is_told_when_a_referenced_document_changes()
    {
        var (p, d, c, _, _, _, _) = await SeedAsync();
        await using var host = await Host();
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server, "uid:ann");

        var resp = await ws.RequestAsync(new JsonObject { ["type"] = "doc.listen", ["path"] = p, ["expand"] = new JsonArray("doctor.clinic") });
        var sub = (string?)resp["result"]!["subscriptionId"];
        var snap = await ws.WaitForAsync(f => (string?)f["type"] == "listen.snapshot" && (string?)f["subscriptionId"] == sub);
        Assert.Equal(2, snap["included"]!.AsObject().Count);   // the doctor and their clinic

        // The clinic is renamed: the patient didn't change, but the subscriber hears about it.
        await Core().WriteAsync([new UpdateWrite { Path = c, Fields = new Dictionary<Documents.FieldPath, Value> { [Documents.FieldPath.Parse("name")] = new StringValue("South") } }]);
        var delta = await ws.WaitForAsync(f => (string?)f["type"] == "listen.delta" && (string?)f["subscriptionId"] == sub);
        Assert.Empty(delta["changes"]!.AsArray());
        Assert.Equal("South", (string?)delta["included"]![c]!["fields"]!["name"]!["stringValue"]);

        // The doctor is deleted: excluded (the patient's field stays a pointer), and so is the clinic only reached through them.
        await Core().WriteAsync([new DeleteWrite { Path = d }]);
        var gone = await ws.WaitForAsync(f => (string?)f["type"] == "listen.delta" && (string?)f["subscriptionId"] == sub && f["excluded"] is not null);
        Assert.Equal(new[] { c, d }.Order(), gone["excluded"]!.AsArray().Select(x => (string)x!).Order());

        // It's back: included again.
        await Core().WriteAsync([new SetWrite { Path = d, Fields = F(("name", new StringValue("Dr Back"))) }]);
        var back = await ws.WaitForAsync(f => (string?)f["type"] == "listen.delta" && (string?)f["subscriptionId"] == sub && f["included"]?[d] is not null);
        Assert.Equal("Dr Back", (string?)back["included"]![d]!["fields"]!["name"]!["stringValue"]);
    }

    [Fact]
    public async Task A_query_subscription_expands_new_results_and_follows_their_references()
    {
        var (p, d, _, _, _, _, _) = await SeedAsync();
        await using var host = await Host();
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server, "uid:ann");
        var collection = p.Split('/')[0];

        var resp = await ws.RequestAsync(new JsonObject { ["type"] = "listen", ["query"] = new JsonObject { ["collection"] = collection }, ["expand"] = new JsonArray("doctor") });
        var sub = (string?)resp["result"]!["subscriptionId"];
        var snap = await ws.WaitForAsync(f => (string?)f["type"] == "listen.snapshot" && (string?)f["subscriptionId"] == sub);
        Assert.True(snap["included"]!.AsObject().ContainsKey(d));

        var other = $"{U("doctors")}/e";
        await Core().WriteAsync([
            new SetWrite { Path = other, Fields = F(("name", new StringValue("Dr Two"))) },
            new SetWrite { Path = $"{collection}/q", Fields = F(("doctor", Ref(other))) },
        ]);
        var added = await ws.WaitForAsync(f => (string?)f["type"] == "listen.delta" && (string?)f["subscriptionId"] == sub && f["changes"]!.AsArray().Count > 0);
        Assert.Equal("Dr Two", (string?)added["included"]![other]!["fields"]!["name"]!["stringValue"]);

        await Core().WriteAsync([new UpdateWrite { Path = other, Fields = new Dictionary<Documents.FieldPath, Value> { [Documents.FieldPath.Parse("name")] = new StringValue("Dr Three") } }]);
        var renamed = await ws.WaitForAsync(f => (string?)f["type"] == "listen.delta" && (string?)f["subscriptionId"] == sub && f["included"]?[other] is not null
            && (string?)f["included"]![other]!["fields"]!["name"]!["stringValue"] == "Dr Three");
        Assert.Empty(renamed["changes"]!.AsArray());
    }

    [Fact]
    public async Task An_expanded_subscription_resumed_with_a_token_still_gets_a_full_snapshot()
    {
        var (p, d, _, _, _, _, _) = await SeedAsync();
        await using var host = await Host();
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server, "uid:ann");
        var first = await ws.RequestAsync(new JsonObject { ["type"] = "doc.listen", ["path"] = p, ["expand"] = new JsonArray("doctor") });
        var sub = (string?)first["result"]!["subscriptionId"];
        var snap = await ws.WaitForAsync(f => (string?)f["type"] == "listen.snapshot" && (string?)f["subscriptionId"] == sub);
        var token = (long)snap["resumeToken"]!;

        // Reconnect-style resume with the current token: a plain subscription would get listen.current.
        var again = await ws.RequestAsync(new JsonObject { ["type"] = "doc.listen", ["path"] = p, ["expand"] = new JsonArray("doctor"), ["resumeToken"] = token });
        var sub2 = (string?)again["result"]!["subscriptionId"];
        var full = await ws.WaitForAsync(f => (string?)f["subscriptionId"] == sub2 && (string?)f["type"] is "listen.snapshot" or "listen.current");
        Assert.Equal("listen.snapshot", (string?)full["type"]);
        Assert.True(full["included"]!.AsObject().ContainsKey(d));
    }

    /// <summary>A resolver of one kind: an in-memory table, with "secret" hidden from callers (not from the app).</summary>
    private sealed class Table(string kind, string channel) : IWincheReferenceResolver
    {
        public System.Collections.Concurrent.ConcurrentDictionary<string, string> Names { get; } = new();
        public System.Collections.Concurrent.ConcurrentBag<ReferenceCaller> Callers { get; } = [];
        public string Kind => kind;
        public string? ChangeChannel => channel;
        public Task<IReadOnlyDictionary<string, JsonObject>> ResolveAsync(IReadOnlyList<string> keys, ReferenceCaller caller, CancellationToken ct)
        {
            Callers.Add(caller);
            IReadOnlyDictionary<string, JsonObject> found = keys
                .Where(k => Names.ContainsKey(k) && (k != "secret" || caller.Trusted))
                .ToDictionary(k => k, k => new JsonObject { ["name"] = Names[k] });
            return Task.FromResult(found);
        }
    }

    [Fact]
    public async Task File_and_user_references_are_resolved_by_their_type_as_the_caller()
    {
        var files = new Table("file", "test_files_changed");
        var users = new Table("user", "test_users_changed");
        files.Names["scans/a.png"] = "Scan A"; files.Names["secret"] = "Hidden";
        users.Names["u2"] = "Dr Lee";
        var owner = $"{U("owners")}/o";
        var item = $"{U("items")}/i";
        await Core().WriteAsync([
            new SetWrite { Path = owner, Fields = F(("author", new UserRefValue("u2"))) },
            new SetWrite { Path = item, Fields = F(("scan", new FileRefValue("scans/a.png")),
                ("more", new ArrayValue([new FileRefValue("secret"), new FileRefValue("nope")])),
                ("plain", new StringValue("scans/a.png")), ("owner", Ref(owner))) },
        ]);
        await using var host = await WsTestHost.StartAsync(Fx.ConnectionString, o =>
        {
            o.UseRules(Rules);
            o.Services.AddSingleton<IWincheReferenceResolver>(files);
            o.Services.AddSingleton<IWincheReferenceResolver>(users);
        });
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server, "uid:ann");

        var get = await ws.RequestAsync(new JsonObject { ["type"] = "doc.get", ["path"] = item, ["expand"] = new JsonArray("scan", "more", "plain", "owner.author") });
        var resolved = get["result"]!["resolved"]!.AsObject();
        Assert.Equal("Scan A", (string?)resolved["file:scans/a.png"]!["name"]);
        Assert.Equal("Dr Lee", (string?)resolved["user:u2"]!["name"]);       // through the owner's document
        Assert.False(resolved.ContainsKey("file:secret"));                   // hidden from the caller
        Assert.False(resolved.ContainsKey("file:nope"));
        Assert.Equal(2, resolved.Count);                                     // a plain string is never resolved
        Assert.Equal("scans/a.png", (string?)get["result"]!["document"]!["fields"]!["scan"]!["fileRefValue"]);
        Assert.Contains(files.Callers, c => c.Uid == "ann" && !c.Trusted);

        // Live: a change a resolver announces reaches the subscriber.
        var resp = await ws.RequestAsync(new JsonObject { ["type"] = "doc.listen", ["path"] = item, ["expand"] = new JsonArray("scan") });
        var sub = (string?)resp["result"]!["subscriptionId"];
        var snap = await ws.WaitForAsync(f => (string?)f["type"] == "listen.snapshot" && (string?)f["subscriptionId"] == sub);
        Assert.Equal("Scan A", (string?)snap["resolved"]!["file:scans/a.png"]!["name"]);
        files.Names["scans/a.png"] = "Renamed";
        await using (var cmd = Fx.DataSource.CreateCommand("SELECT pg_notify('test_files_changed', '')")) await cmd.ExecuteNonQueryAsync();
        var delta = await ws.WaitForAsync(f => (string?)f["type"] == "listen.delta" && (string?)f["subscriptionId"] == sub && f["resolved"] is not null);
        Assert.Equal("Renamed", (string?)delta["resolved"]!["file:scans/a.png"]!["name"]);
        files.Names.TryRemove("scans/a.png", out _);
        await using (var cmd = Fx.DataSource.CreateCommand("SELECT pg_notify('test_files_changed', '')")) await cmd.ExecuteNonQueryAsync();
        var gone = await ws.WaitForAsync(f => (string?)f["type"] == "listen.delta" && (string?)f["subscriptionId"] == sub && f["unresolved"] is not null);
        Assert.Equal(["file:scans/a.png"], gone["unresolved"]!.AsArray().Select(x => (string)x!));
    }

    [Fact]
    public async Task File_and_user_references_are_queried_and_ordered_by_type()
    {
        var c = U("refq");
        await Core().WriteAsync([
            new SetWrite { Path = $"{c}/a", Fields = F(("who", new UserRefValue("u1"))) },
            new SetWrite { Path = $"{c}/b", Fields = F(("who", new ReferenceValue("people/p1"))) },
            new SetWrite { Path = $"{c}/e", Fields = F(("who", new StringValue("u1"))) },
            new SetWrite { Path = $"{c}/c", Fields = F(("who", new FileRefValue("u1"))) },
            new SetWrite { Path = $"{c}/d", Fields = F(("who", new UserRefValue("u2"))) },
        ]);

        async Task<string[]> Ids(Value v) => [.. (await Core().QueryAsync(new Querying.Ast.Query(c,
            new Querying.Ast.FieldFilter(Documents.FieldPath.Parse("who"), Querying.Ast.FilterOperator.Eq, v)))).Documents.Select(d => d.Id)];
        Assert.Equal(["a"], await Ids(new UserRefValue("u1")));          // not the file reference, not the string
        Assert.Equal(["c"], await Ids(new FileRefValue("u1")));
        Assert.Equal(["b"], await Ids(new ReferenceValue("people/p1")));
        Assert.Equal(["e"], await Ids(new StringValue("u1")));

        var ordered = await Core().QueryAsync(new Querying.Ast.Query(c, null, [new Querying.Ast.Ordering(Documents.FieldPath.Parse("who"), Querying.Ast.SortDirection.Asc)]));
        Assert.Equal(["e", "b", "c", "a", "d"], ordered.Documents.Select(d => d.Id));   // strings, document refs, files, users
    }
}
