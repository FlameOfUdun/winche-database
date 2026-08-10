using System.Text.Json.Nodes;
using Winche.Database.AspNetCore.WebSockets.Connections;
using Winche.Database.DependencyInjection;
using Winche.Database.Querying.Planning;
using Winche.Rules;
using Winche.Rules.Expressions;

namespace Winche.Database.IntegrationTests.Ws;

/// <summary>
/// Covers the configurable per-connection/request-shape ceilings enforced at the WS boundary:
/// <see cref="QueryLimits"/> for <c>query</c>/<c>doc.getAll</c>, and
/// <see cref="WsOptions.MaxSubscriptionsPerConnection"/> / <see cref="WsOptions.MaxTransactionsPerConnection"/>.
/// </summary>
[Collection("postgres")]
public class WsRequestLimitsTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private static void AllowAll(WincheDatabaseOptions c) =>
        c.UseRules(r => r.Match("{document=**}", b => b.Allow(RuleOperations.All, Expr.Const(true))));

    [Fact]
    public async Task Listen_QueryOverCeiling_RejectedWithoutClosingConnection()
    {
        await using var host = await WsTestHost.StartAsync(Fx.ConnectionString, c =>
        {
            AllowAll(c);
            c.QueryLimits = new QueryLimits { MaxOrderByKeys = 1 };
        });
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server);

        var rejected = await ws.RequestAsync(new JsonObject
        {
            ["type"] = "listen",
            ["query"] = new JsonObject
            {
                ["collection"] = "c",
                ["orderBy"] = new JsonArray(
                    new JsonObject { ["field"] = "a" },
                    new JsonObject { ["field"] = "b" }),
            },
            ["protocol"] = 3,
        });
        Assert.Equal(("error", "INVALID_QUERY"), ((string?)rejected["type"], (string?)rejected["status"]));
        Assert.Equal("TOO_MANY_ORDER_BY_KEYS", (string?)rejected["details"]?["code"]);

        // connection must still be usable
        var ping = await ws.RequestAsync(new JsonObject { ["type"] = "ping" });
        Assert.Equal("response", (string?)ping["type"]);
    }

    [Fact]
    public async Task DocGetAll_OverCeiling_RejectedWithoutClosingConnection()
    {
        await using var host = await WsTestHost.StartAsync(Fx.ConnectionString, c =>
        {
            AllowAll(c);
            c.QueryLimits = new QueryLimits { MaxBatchGetSize = 2 };
        });
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server);

        var atLimit = await ws.RequestAsync(new JsonObject
        {
            ["type"] = "doc.getAll",
            ["paths"] = new JsonArray("a/1", "a/2"),
        });
        Assert.Equal("response", (string?)atLimit["type"]);

        var overLimit = await ws.RequestAsync(new JsonObject
        {
            ["type"] = "doc.getAll",
            ["paths"] = new JsonArray("a/1", "a/2", "a/3"),
        });
        Assert.Equal(("error", "INVALID_ARGUMENT"), ((string?)overLimit["type"], (string?)overLimit["status"]));

        var ping = await ws.RequestAsync(new JsonObject { ["type"] = "ping" });
        Assert.Equal("response", (string?)ping["type"]);
    }

    [Fact]
    public async Task Listen_OverSubscriptionCap_RejectedWithoutClosingConnection()
    {
        await using var host = await WsTestHost.StartAsync(Fx.ConnectionString,
            c => AllowAll(c),
            ws => ws.MaxSubscriptionsPerConnection = 2);
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server);

        static JsonObject Listen(string collection) => new()
        {
            ["type"] = "listen",
            ["query"] = new JsonObject { ["collection"] = collection },
            ["protocol"] = 3,
        };

        var sub1 = await ws.RequestAsync(Listen("c1"));
        Assert.Equal("response", (string?)sub1["type"]);
        var sub2 = await ws.RequestAsync(Listen("c2"));
        Assert.Equal("response", (string?)sub2["type"]);

        var sub3 = await ws.RequestAsync(Listen("c3"));
        Assert.Equal(("error", "INVALID_ARGUMENT"), ((string?)sub3["type"], (string?)sub3["status"]));

        // connection stays open and usable
        var ping = await ws.RequestAsync(new JsonObject { ["type"] = "ping" });
        Assert.Equal("response", (string?)ping["type"]);

        // freeing a subscription slot lets a new one through
        var subId1 = (string)sub1["result"]!["subscriptionId"]!;
        var unlisten = await ws.RequestAsync(new JsonObject { ["type"] = "unlisten", ["subscriptionId"] = subId1 });
        Assert.Equal("response", (string?)unlisten["type"]);

        var sub4 = await ws.RequestAsync(Listen("c4"));
        Assert.Equal("response", (string?)sub4["type"]);
    }

    [Fact]
    public async Task TxBegin_OverTransactionCap_RejectedWithoutClosingConnection()
    {
        await using var host = await WsTestHost.StartAsync(Fx.ConnectionString,
            c => AllowAll(c),
            ws => ws.MaxTransactionsPerConnection = 2);
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server);

        var begin1 = await ws.RequestAsync(new JsonObject { ["type"] = "tx.begin" });
        Assert.Equal("response", (string?)begin1["type"]);
        var begin2 = await ws.RequestAsync(new JsonObject { ["type"] = "tx.begin" });
        Assert.Equal("response", (string?)begin2["type"]);

        var begin3 = await ws.RequestAsync(new JsonObject { ["type"] = "tx.begin" });
        Assert.Equal(("error", "INVALID_ARGUMENT"), ((string?)begin3["type"], (string?)begin3["status"]));

        var ping = await ws.RequestAsync(new JsonObject { ["type"] = "ping" });
        Assert.Equal("response", (string?)ping["type"]);
    }

    [Fact]
    public async Task TxBegin_PrunesLedgerExpiredIds_BeforeCheckingCap()
    {
        await using var host = await WsTestHost.StartAsync(Fx.ConnectionString,
            c =>
            {
                AllowAll(c);
                c.TransactionConfig = new TransactionConfig
                {
                    TotalTimeoutSpan = TimeSpan.FromMilliseconds(150),
                    IdleTimeoutSpan = TimeSpan.FromMilliseconds(150),
                };
            },
            ws => ws.MaxTransactionsPerConnection = 1);
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server);

        var begin1 = await ws.RequestAsync(new JsonObject { ["type"] = "tx.begin" });
        Assert.Equal("response", (string?)begin1["type"]);

        // begin1's transaction is now the only "slot"; without pruning, begin2 would be rejected.
        await Task.Delay(400);   // outlives TotalTimeoutSpan — begin1's id is now stale in the ledger

        var begin2 = await ws.RequestAsync(new JsonObject { ["type"] = "tx.begin" });
        Assert.Equal("response", (string?)begin2["type"]);
    }
}
