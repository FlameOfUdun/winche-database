using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Winche.Database.AspNetCore.WebSockets.Connections;
using Winche.Database.AspNetCore.WebSockets.Protocol;
using Winche.Database.DependencyInjection;
using Winche.Database.Documents;
using Winche.Database.Querying;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Planning;
using Winche.Database.Runtime;
using Winche.Database.Runtime.ChangeFeed;
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
public sealed class MessageRouter(IOptions<WincheDatabaseOptions> dbOptions, IOptions<WsOptions> wsOptions)
{
    private readonly QueryLimits _queryLimits = dbOptions.Value.QueryLimits;
    private readonly TransactionConfig _txConfig = dbOptions.Value.TransactionConfig;
    private readonly WsOptions _wsOptions = wsOptions.Value;

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
                DocGetAllMessage getAll => Ok(id!, new JsonObject { ["documents"] = new JsonArray([.. (await GetAllValidatedAsync(scope, getAll.Paths, ct)).Select(ToNode)]) }),
                QueryMessage query => Ok(id!, ToNode(await QueryValidatedAsync(scope, query.Query, ct))!.AsObject()),
                CountMessage count => Ok(id!, new JsonObject { ["count"] = await CountValidatedAsync(scope, count.Query, ct) }),
                AggregateMessage agg => Ok(id!, new JsonObject { ["result"] = AggregateResultBody(await AggregateValidatedAsync(scope, agg.Query, agg.Aggregations, ct)) }),
                AddMessage add => Ok(id!, new JsonObject { ["document"] = ToNode(await scope.Db.AddAsync(add.Collection, add.Fields, ct)) }),
                WriteMessage write => Ok(id!, WriteResultsBody(await scope.Db.WriteAsync(write.Writes, ct))),
                TxBeginMessage => await HandleTxBegin(scope, id!, ct),
                TxGetMessage txGet => Ok(id!, new JsonObject { ["document"] = ToNode(await scope.Db.GetAsync(RequireTx(scope, txGet.TransactionId), txGet.Path, ct)) }),
                TxQueryMessage txQuery => Ok(id!, ToNode(await TxQueryValidatedAsync(scope, RequireTx(scope, txQuery.TransactionId), txQuery.Query, ct))!.AsObject()),
                TxCommitMessage txCommit => await HandleTxCommit(scope, id!, txCommit, ct),
                TxRollbackMessage txRollback => await HandleTxRollback(scope, id!, txRollback, ct),
                ListenMessage listen => HandleListen(scope, conn, id!, listen.Query, listen.ResumeToken, listen.Protocol),
                DocListenMessage docListen => await HandleDocListen(scope, conn, id!, docListen.Path, docListen.ResumeToken, docListen.Protocol, ct),
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

    // ── Query-shape / batch-size validation (shared by the one-shot and transactional forms) ──

    private Task<IReadOnlyList<Document?>> GetAllValidatedAsync(ConnectionScope scope, IReadOnlyList<string> paths, CancellationToken ct)
    {
        if (paths.Count > _queryLimits.MaxBatchGetSize)
            throw new RuntimeException(RuntimeStatus.InvalidArgument,
                $"'paths' may contain at most {_queryLimits.MaxBatchGetSize} paths, got {paths.Count}.");
        return scope.Db.GetAllAsync(paths, ct);
    }

    private Task<QueryResult> QueryValidatedAsync(ConnectionScope scope, Query query, CancellationToken ct)
    {
        QueryShapeValidator.Validate(query, _queryLimits);
        return scope.Db.QueryAsync(query, ct);
    }

    private Task<QueryResult> TxQueryValidatedAsync(ConnectionScope scope, string transactionId, Query query, CancellationToken ct)
    {
        QueryShapeValidator.Validate(query, _queryLimits);
        return scope.Db.QueryAsync(transactionId, query, ct);
    }

    private Task<long> CountValidatedAsync(ConnectionScope scope, Query query, CancellationToken ct)
    {
        QueryShapeValidator.Validate(query, _queryLimits);
        return scope.Db.CountAsync(query, ct);
    }

    private Task<AggregationResult> AggregateValidatedAsync(
        ConnectionScope scope, Query query, IReadOnlyList<Aggregation> aggregations, CancellationToken ct)
    {
        QueryShapeValidator.Validate(query, _queryLimits);
        return scope.Db.AggregateAsync(query, aggregations, ct);
    }

    // ── Transactions ─────────────────────────────────────────────────────────────

    private async Task<ServerMessage> HandleTxBegin(ConnectionScope scope, string id, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        lock (scope.Gate)
        {
            scope.PruneExpiredTransactions(_txConfig.TotalTimeoutSpan, now);
            if (scope.Transactions.Count >= _wsOptions.MaxTransactionsPerConnection)
                throw new RuntimeException(RuntimeStatus.InvalidArgument,
                    $"This connection already has {scope.Transactions.Count} open transactions, the maximum of {_wsOptions.MaxTransactionsPerConnection}.");
        }

        var handle = await scope.Db.BeginTransactionAsync(ct);
        lock (scope.Gate)
        {
            scope.Transactions.Add(handle.Id);
            scope.TransactionBeganAt[handle.Id] = now;
        }
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
            lock (scope.Gate)
            {
                scope.Transactions.Remove(txId);
                scope.TransactionBeganAt.Remove(txId);
            }
        }
    }

    private static async Task<ServerMessage> HandleTxRollback(
        ConnectionScope scope,
        string id,
        TxRollbackMessage msg,
        CancellationToken ct)
    {
        bool owned;
        lock (scope.Gate)
        {
            owned = scope.Transactions.Remove(msg.TransactionId);
            scope.TransactionBeganAt.Remove(msg.TransactionId);
        }
        if (owned)
            await scope.Db.RollbackTransactionAsync(msg.TransactionId, ct);
        return Ok(id, []);
    }

    // ── Listeners ────────────────────────────────────────────────────────────────

    private ServerMessage HandleListen(ConnectionScope scope, WsConnection conn, string id, Query query, string? resumeToken, long? clientProtocol)
    {
        QueryShapeValidator.Validate(query, _queryLimits);
        EnsureSubscriptionCapacity(scope);

        var listener = scope.Db.Listen(query, ParseResumeToken(resumeToken));
        var subscriptionId = Guid.NewGuid().ToString("N");
        var cts = CancellationTokenSource.CreateLinkedTokenSource(conn.Closed);
        var supportsDeleted = clientProtocol >= SubscriptionPump.DeletedKindMinProtocol;
        var pump = SubscriptionPump.RunQueryAsync(subscriptionId, listener, scope, conn, supportsDeleted, cts.Token);
        return Register(scope, subscriptionId, listener, pump, cts, id);
    }

    private async Task<ServerMessage> HandleDocListen(
        ConnectionScope scope, WsConnection conn, string id, string path, string? resumeToken, long? clientProtocol, CancellationToken ct)
    {
        EnsureSubscriptionCapacity(scope);

        var options = ParseResumeToken(resumeToken);
        var listener = await scope.Db.ListenToDocumentAsync(path, options, ct);   // get-authorized; throws → mapped to error frame
        var subscriptionId = Guid.NewGuid().ToString("N");
        var cts = CancellationTokenSource.CreateLinkedTokenSource(conn.Closed);
        var supportsDeleted = clientProtocol >= SubscriptionPump.DeletedKindMinProtocol;
        var pump = SubscriptionPump.RunDocumentAsync(subscriptionId, listener, scope, conn, supportsDeleted, cts.Token);
        return Register(scope, subscriptionId, listener, pump, cts, id);
    }

    // resumeToken travels as the opaque string ChangeCursor.ToString() produces (see ClientMessage) —
    // a JSON number can't safely carry the full xid8 range. A malformed token is a client mistake,
    // reported the same way as any other bad argument, not a server fault.
    private static ListenOptions? ParseResumeToken(string? resumeToken)
    {
        if (resumeToken is null) return null;
        // TryParse rather than catch: an over-range component overflows instead of mis-formatting,
        // and letting that escape would turn a bad client argument into a server fault. The token is
        // not echoed back — it is the caller's own input and carries no diagnostic value here.
        if (!ChangeCursor.TryParse(resumeToken, out var cursor))
            throw new RuntimeException(RuntimeStatus.InvalidArgument, "Invalid resumeToken.");
        return new ListenOptions(ResumeFrom: cursor);
    }

    private void EnsureSubscriptionCapacity(ConnectionScope scope)
    {
        lock (scope.Gate)
        {
            if (scope.Subscriptions.Count >= _wsOptions.MaxSubscriptionsPerConnection)
                throw new RuntimeException(RuntimeStatus.InvalidArgument,
                    $"This connection already has {scope.Subscriptions.Count} open subscriptions, the maximum of {_wsOptions.MaxSubscriptionsPerConnection}.");
        }
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
