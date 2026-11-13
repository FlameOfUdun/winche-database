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
        Assert.Equal(d, (string?)get["result"]!["document"]!["fields"]!["doctor"]!["referenceValue"]);   // the field itself stays a pointer

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
}
