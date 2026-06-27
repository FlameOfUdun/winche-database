using System.Text.Json;
using System.Text.Json.Nodes;
using Winche.Database.AspNetCore.WebSockets.Connections;
using Winche.Database.AspNetCore.WebSockets.Protocol;
using Winche.Database.Querying;
using Winche.Database.Querying.Ast;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Listening;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;
using Winche.Database.Wire; // ErrorMapper

namespace Winche.Database.AspNetCore.WebSockets.Routing;

/// <summary>
/// Dispatches one client message to one terminal frame. Inbound processing is
/// serial per connection (the endpoint awaits HandleAsync); listener events flow separately
/// through the connection's send channel via SubscriptionPump.
/// </summary>
public sealed class MessageRouter
{
    public async Task<ServerMessage> HandleAsync(
        ConnectionScope scope, WsConnection conn, Microsoft.AspNetCore.Http.HttpContext httpContext,
        ClientMessage message, CancellationToken ct)
    {
        var id = message.Id;
        if (id is null)
            return new ErrorMessage { Status = "INVALID_ARGUMENT", Message = "'id' is required." };

        try
        {
            scope.ApplyClaims();
            return message switch
            {
                PingMessage => Ok(id!, []),
                DocGetMessage get => Ok(id!, new JsonObject { ["document"] = ToNode(await scope.Db.GetAsync(get.Path, ct)) }),
                DocGetAllMessage getAll => Ok(id!, new JsonObject { ["documents"] = new JsonArray([.. (await scope.Db.GetAllAsync(getAll.Paths, ct)).Select(ToNode)]) }),
                QueryMessage query => Ok(id!, ToNode(await scope.Db.QueryAsync(query.Query, ct))!.AsObject()),
                CountMessage count => Ok(id!, new JsonObject { ["count"] = await scope.Db.CountAsync(count.Query, ct) }),
                AggregateMessage agg => Ok(id!, new JsonObject { ["result"] = AggregateResultBody(await scope.Db.AggregateAsync(agg.Query, agg.Aggregations, ct)) }),
                AddMessage add => Ok(id!, new JsonObject { ["document"] = ToNode(await scope.Db.AddAsync(add.Collection, add.Fields, ct)) }),
                WriteMessage write => Ok(id!, WriteResultsBody(await scope.Db.WriteAsync(write.Writes, ct))),
                TxBeginMessage => await HandleTxBegin(scope, id!, ct),
                TxGetMessage txGet => Ok(id!, new JsonObject { ["document"] = ToNode(await scope.Db.GetAsync(RequireTx(scope, txGet.TransactionId), txGet.Path, ct)) }),
                TxQueryMessage txQuery => Ok(id!, ToNode(await scope.Db.QueryAsync(RequireTx(scope, txQuery.TransactionId), txQuery.Query, ct))!.AsObject()),
                TxCommitMessage txCommit => await HandleTxCommit(scope, id!, txCommit, ct),
                TxRollbackMessage txRollback => await HandleTxRollback(scope, id!, txRollback, ct),
                ListenMessage listen => HandleListen(scope, conn, id!, listen.Query, listen.ResumeToken),
                DocListenMessage docListen => await HandleDocListen(scope, conn, id!, docListen.Path, docListen.ResumeToken, ct),
                UnlistenMessage unlisten => await HandleUnlisten(scope, id!, unlisten),
                _ => new ErrorMessage { Id = id, Status = "INVALID_ARGUMENT", Message = $"Unknown message: {message.GetType().Name}" },
            };
        }
        catch (Exception ex)
        {
            var error = ErrorMapper.Map(ex);
            return new ErrorMessage { Id = id, Status = error.Status, Message = error.Message, Details = error.Details };
        }
    }

    private static async Task<ServerMessage> HandleTxBegin(ConnectionScope scope, string id, CancellationToken ct)
    {
        var handle = await scope.Db.BeginTransactionAsync(ct);
        lock (scope.Gate) scope.Transactions.Add(handle.Id);
        return Ok(id, new JsonObject { ["transactionId"] = handle.Id });
    }

    private static async Task<ServerMessage> HandleTxCommit(
        ConnectionScope scope, string id, TxCommitMessage msg, CancellationToken ct)
    {
        var txId = RequireTx(scope, msg.TransactionId);
        try
        {
            var results = await scope.Db.CommitTransactionAsync(txId, msg.Writes, ct);
            return Ok(id, WriteResultsBody(results));
        }
        finally
        {
            lock (scope.Gate) scope.Transactions.Remove(txId);
        }
    }

    private static async Task<ServerMessage> HandleTxRollback(
        ConnectionScope scope,
        string id,
        TxRollbackMessage msg,
        CancellationToken ct)
    {
        bool owned;
        lock (scope.Gate) owned = scope.Transactions.Remove(msg.TransactionId);
        if (owned)
            await scope.Db.RollbackTransactionAsync(msg.TransactionId, ct);
        return Ok(id, []);
    }

    private static ServerMessage HandleListen(ConnectionScope scope, WsConnection conn, string id, Query query, long? resumeToken)
    {
        var listener = scope.Db.Listen(query, resumeToken is { } seq ? new ListenOptions(ResumeFrom: seq) : null);
        var subscriptionId = Guid.NewGuid().ToString("N");
        var cts = CancellationTokenSource.CreateLinkedTokenSource(conn.Closed);
        var pump = SubscriptionPump.RunQueryAsync(subscriptionId, listener, scope, conn, cts.Token);
        return Register(scope, subscriptionId, listener, pump, cts, id);
    }

    private static async Task<ServerMessage> HandleDocListen(
        ConnectionScope scope, WsConnection conn, string id, string path, long? resumeToken, CancellationToken ct)
    {
        var options = resumeToken is { } seq ? new ListenOptions(ResumeFrom: seq) : null;
        var listener = await scope.Db.ListenToDocumentAsync(path, options, ct);   // get-authorized; throws → mapped to error frame
        var subscriptionId = Guid.NewGuid().ToString("N");
        var cts = CancellationTokenSource.CreateLinkedTokenSource(conn.Closed);
        var pump = SubscriptionPump.RunDocumentAsync(subscriptionId, listener, scope, conn, cts.Token);
        return Register(scope, subscriptionId, listener, pump, cts, id);
    }

    private static ServerMessage Register(
        ConnectionScope scope, string subscriptionId, IAsyncDisposable listener, Task pump, CancellationTokenSource cts, string id)
    {
        lock (scope.Gate) scope.Subscriptions[subscriptionId] = new ConnectionScope.Subscription(listener, pump, cts);
        return Ok(id, new JsonObject { ["subscriptionId"] = subscriptionId });
    }

    private static async Task<ServerMessage> HandleUnlisten(ConnectionScope scope, string id, UnlistenMessage msg)
    {
        ConnectionScope.Subscription? sub;
        lock (scope.Gate)
        {
            scope.Subscriptions.Remove(msg.SubscriptionId, out sub);
        }
        if (sub is not null)
        {
            sub.Cts.Cancel();
            try { await sub.Listener.DisposeAsync(); } catch { }
        }
        return Ok(id, []);
    }

    private static string RequireTx(ConnectionScope scope, string transactionId)
    {
        lock (scope.Gate)
        {
            if (!scope.Transactions.Contains(transactionId))
                throw new RuntimeException(RuntimeStatus.Aborted,
                    $"Transaction '{transactionId}' is not active on this connection.");
        }
        return transactionId;
    }

    private static ResponseMessage Ok(string id, JsonObject result) => new() { Id = id, Result = result };

    private static JsonObject WriteResultsBody(IReadOnlyList<WriteResult> results) =>
        new() { ["writeResults"] = new JsonArray([.. results.Select(r => ToNode(r))]) };

    private static JsonObject AggregateResultBody(AggregationResult result)
    {
        var obj = new JsonObject();
        foreach (var (alias, value) in result.Values)
            obj[alias] = ValueSerializer.Write(value);
        return obj;
    }

    private static JsonNode? ToNode<T>(T? value) =>
        value is null ? null : JsonSerializer.SerializeToNode(value, value.GetType());
}
