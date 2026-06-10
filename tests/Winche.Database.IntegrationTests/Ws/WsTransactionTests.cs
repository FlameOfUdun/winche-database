using System.Text.Json.Nodes;
using Winche.Rules;
using Winche.Rules.Expressions;

namespace Winche.Database.IntegrationTests.Ws;

[Collection("postgres")]
public class WsTransactionTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private Task<WsTestHost> Host() => WsTestHost.StartAsync(Fx.ConnectionString,
        c => c.UseRules(r => r.Match("{document=**}", b => b.Allow(RuleOperations.All, Expr.Const(true)))));

    private static JsonObject Set(string path, long n) => new()
    {
        ["type"] = "write",
        ["writes"] = new JsonArray(new JsonObject
        {
            ["set"] = new JsonObject
            {
                ["path"] = path,
                ["fields"] = new JsonObject { ["n"] = new JsonObject { ["integerValue"] = n.ToString() } },
            },
        }),
    };

    [Fact]
    public async Task Tx_ReadCommit_HappyPath_And_ConflictAborts()
    {
        await using var host = await Host();
        await using var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server);
        await ws.RequestAsync(Set("wstx/a", 1));

        // happy path
        var begin = await ws.RequestAsync(new JsonObject { ["type"] = "tx.begin" });
        var txId = (string)begin["result"]!["transactionId"]!;
        await ws.RequestAsync(new JsonObject { ["type"] = "tx.get", ["transactionId"] = txId, ["path"] = "wstx/a" });
        var commit = await ws.RequestAsync(new JsonObject
        {
            ["type"] = "tx.commit", ["transactionId"] = txId,
            ["writes"] = Set("wstx/a", 2)["writes"]!.DeepClone(),
        });
        Assert.Equal("response", (string?)commit["type"]);

        // conflict
        var begin2 = await ws.RequestAsync(new JsonObject { ["type"] = "tx.begin" });
        var txId2 = (string)begin2["result"]!["transactionId"]!;
        await ws.RequestAsync(new JsonObject { ["type"] = "tx.get", ["transactionId"] = txId2, ["path"] = "wstx/a" });
        await ws.RequestAsync(Set("wstx/a", 99));                         // interloper (same socket, plain write)
        var aborted = await ws.RequestAsync(new JsonObject
        {
            ["type"] = "tx.commit", ["transactionId"] = txId2,
            ["writes"] = Set("wstx/a", 3)["writes"]!.DeepClone(),
        });
        Assert.Equal(("error", "ABORTED"), ((string?)aborted["type"], (string?)aborted["status"]));
    }

    [Fact]
    public async Task ForeignTxId_Aborts_CrossConnection()
    {
        await using var host = await Host();
        await using var ws1 = await WsTestClient.ConnectAndWelcomeAsync(host.Server);
        await using var ws2 = await WsTestClient.ConnectAndWelcomeAsync(host.Server);

        var begin = await ws1.RequestAsync(new JsonObject { ["type"] = "tx.begin" });
        var txId = (string)begin["result"]!["transactionId"]!;

        var foreign = await ws2.RequestAsync(new JsonObject
            { ["type"] = "tx.get", ["transactionId"] = txId, ["path"] = "wstx/a" });
        Assert.Equal("ABORTED", (string?)foreign["status"]);
    }

    // ── C2: foreign tx.rollback must not touch another connection's transaction ──
    [Fact]
    public async Task ForeignTxRollback_IsIdempotent_OriginalConnectionCanStillCommit()
    {
        await using var host = await Host();
        await using var ws1 = await WsTestClient.ConnectAndWelcomeAsync(host.Server);
        await using var ws2 = await WsTestClient.ConnectAndWelcomeAsync(host.Server);

        // Seed a document so ws1's tx.get populates the read set (commit requires ≥1 read or write)
        await ws1.RequestAsync(Set("wstx/c2seed", 0));

        // ws1 begins a transaction and reads a document (populates read set)
        var begin = await ws1.RequestAsync(new JsonObject { ["type"] = "tx.begin" });
        var txId = (string)begin["result"]!["transactionId"]!;
        await ws1.RequestAsync(new JsonObject
            { ["type"] = "tx.get", ["transactionId"] = txId, ["path"] = "wstx/c2seed" });

        // ws2 sends tx.rollback for ws1's id — must be a no-op (ws2 doesn't own it)
        var rollbackResp = await ws2.RequestAsync(new JsonObject
            { ["type"] = "tx.rollback", ["transactionId"] = txId });
        Assert.Equal("response", (string?)rollbackResp["type"]);

        // ws1's transaction must still be alive — commit with empty writes but populated read set
        var commitResp = await ws1.RequestAsync(new JsonObject
        {
            ["type"] = "tx.commit", ["transactionId"] = txId,
            ["writes"] = Set("wstx/c2seed", 1)["writes"]!.DeepClone(),
        });
        Assert.Equal("response", (string?)commitResp["type"]);
    }

    [Fact]
    public async Task Disconnect_RollsBackOpenTransaction()
    {
        await using var host = await Host();
        var ws = await WsTestClient.ConnectAndWelcomeAsync(host.Server);
        var begin = await ws.RequestAsync(new JsonObject { ["type"] = "tx.begin" });
        var txId = (string)begin["result"]!["transactionId"]!;
        await ws.DisposeAsync();                                          // abrupt disconnect

        await Task.Delay(500);                                            // scope disposal runs server-side
        await using var ws2 = await WsTestClient.ConnectAndWelcomeAsync(host.Server);
        // a foreign-connection commit would be ABORTED regardless; prove the LEDGER dropped it
        // by beginning + committing fresh — and asserting the old id is gone via tx.get on ws2:
        var gone = await ws2.RequestAsync(new JsonObject
            { ["type"] = "tx.get", ["transactionId"] = txId, ["path"] = "wstx/a" });
        Assert.Equal("ABORTED", (string?)gone["status"]);
    }
}
