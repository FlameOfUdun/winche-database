using System.Text.Json;
using System.Text.Json.Nodes;
using Winche.Database.AspNetCore.WebSockets.Connections;
using Winche.Database.AspNetCore.WebSockets.Protocol;
using Winche.Database.Querying;
using Winche.Database.Querying.Ast;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Listening;
using Winche.Database.Runtime.Writes;
using Winche.Values;
using Winche.Database.Wire; // ErrorMapper
using Winche.Database.Documents;
using Winche.Database.Runtime.Expansion;

namespace Winche.Database.AspNetCore.WebSockets.Routing;

/// <summary>
/// Dispatches one client message to one terminal frame. Inbound processing is
/// serial per connection (the endpoint awaits HandleAsync); listener events flow separately
/// through the connection's send channel via SubscriptionPump.
/// </summary>
public sealed class MessageRouter
{
    private readonly Microsoft.Extensions.Logging.ILogger _logger;

    /// <summary>A router that doesn't log (tests).</summary>
    public MessageRouter() : this(Microsoft.Extensions.Logging.Abstractions.NullLogger<MessageRouter>.Instance) { }

    /// <summary>A router that logs unexpected errors (with an error id the client also gets).</summary>
    public MessageRouter(Microsoft.Extensions.Logging.ILogger<MessageRouter> logger) => _logger = logger;

    public async Task<ServerMessage> HandleAsync(
        ConnectionScope scope, WsConnection conn, Microsoft.AspNetCore.Http.HttpContext httpContext,
        ClientMessage message, CancellationToken ct)
    {
        var id = message.Id;
        if (id is null)
            return new ErrorMessage { Error = new WincheError(WincheErrorCode.InvalidArgument, "'id' is required.") };

        try
        {
            scope.ApplyClaims();
            return message switch
            {
                PingMessage => Ok(id!, []),
                DocGetMessage get => await HandleGet(scope, id!, get, ct),
                DocGetAllMessage getAll => await HandleGetAll(scope, id!, getAll, ct),
                QueryMessage query => await HandleQuery(scope, id!, query, ct),
                CountMessage count => Ok(id!, new JsonObject { ["count"] = await scope.Db.CountAsync(count.Query, ct) }),
                AggregateMessage agg => Ok(id!, new JsonObject { ["result"] = AggregateResultBody(await scope.Db.AggregateAsync(agg.Query, agg.Aggregations, ct)) }),
                AddMessage add => Ok(id!, new JsonObject { ["document"] = ToNode(await scope.Db.AddAsync(add.Collection, add.Fields, ct)) }),
                WriteMessage write => Ok(id!, WriteResultsBody(await scope.Db.WriteAsync(write.Writes, ct))),
                TxBeginMessage => await HandleTxBegin(scope, id!, ct),
                TxGetMessage txGet => Ok(id!, new JsonObject { ["document"] = ToNode(await scope.Db.GetAsync(RequireTx(scope, txGet.TransactionId), txGet.Path, ct)) }),
                TxQueryMessage txQuery => Ok(id!, ToNode(await scope.Db.QueryAsync(RequireTx(scope, txQuery.TransactionId), txQuery.Query, ct))!.AsObject()),
                TxCommitMessage txCommit => await HandleTxCommit(scope, id!, txCommit, ct),
                TxRollbackMessage txRollback => await HandleTxRollback(scope, id!, txRollback, ct),
                ListenMessage listen => HandleListen(scope, conn, id!, listen.Query, listen.ResumeToken, listen.Protocol, ExpandRequest.Of(listen.Expand)),
                DocListenMessage docListen => await HandleDocListen(scope, conn, id!, docListen.Path, docListen.ResumeToken, docListen.Protocol,
                    ExpandRequest.Of(docListen.Expand), ct),
                UnlistenMessage unlisten => await HandleUnlisten(scope, id!, unlisten),
                _ => new ErrorMessage { Id = id, Error = new WincheError(WincheErrorCode.InvalidArgument, $"Unknown message: {message.GetType().Name}") },
            };
        }
        catch (Exception ex)
        {
            var error = ErrorMapper.Map(ex, _logger, $"WebSocket {MessageType(message)}", scope.UserId);
            return new ErrorMessage { Id = id, Error = error };
        }
    }

    private static async Task<ServerMessage> HandleGet(ConnectionScope scope, string id, DocGetMessage get, CancellationToken ct)
    {
        var expand = ExpandRequest.Of(get.Expand);
        var doc = await scope.Db.GetAsync(get.Path, ct);
        var body = new JsonObject { ["document"] = ToNode(doc) };
        return Ok(id, await WithIncluded(scope, body, [doc], expand, ct));
    }

    private static async Task<ServerMessage> HandleGetAll(ConnectionScope scope, string id, DocGetAllMessage getAll, CancellationToken ct)
    {
        var expand = ExpandRequest.Of(getAll.Expand);
        var docs = await scope.Db.GetAllAsync(getAll.Paths, ct);
        var body = new JsonObject { ["documents"] = new JsonArray([.. docs.Select(ToNode)]) };
        return Ok(id, await WithIncluded(scope, body, docs, expand, ct));
    }

    private static async Task<ServerMessage> HandleQuery(ConnectionScope scope, string id, QueryMessage query, CancellationToken ct)
    {
        var expand = ExpandRequest.Of(query.Expand);
        var result = await scope.Db.QueryAsync(query.Query, ct);
        return Ok(id, await WithIncluded(scope, ToNode(result)!.AsObject(), result.Documents, expand, ct));
    }

    /// <summary>Adds <c>included</c> (the readable referenced documents, by path) when references were asked for.</summary>
    private static async Task<JsonObject> WithIncluded(ConnectionScope scope, JsonObject body, IEnumerable<Document?> docs, ExpandRequest? expand, CancellationToken ct)
    {
        if (expand is null) return body;
        var expansion = await ReferenceExpander.ExpandAsync(scope.Db, docs, expand, ct);
        body["included"] = Included(expansion.Included);
        return body;
    }

    internal static JsonObject Included(IReadOnlyDictionary<string, Document> docs)
    {
        var o = new JsonObject();
        foreach (var (path, doc) in docs) o[path] = ToNode(doc);
        return o;
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

    private static ErrorMessage TooManySubscriptions(ConnectionScope scope, string id)
    {
        Winche.Database.Runtime.WincheDatabaseMetrics.WsLimited.Add(1, Winche.Database.Runtime.WincheDatabaseMetrics.Tags("limit", "subscriptions"));
        return TooManySubscriptionsError(scope, id);
    }

    private static ErrorMessage TooManySubscriptionsError(ConnectionScope scope, string id) => new()
    {
        Id = id,
        Error = new WincheError(WincheErrorCode.ResourceExhausted,
            $"This connection already has {scope.MaxSubscriptions} live subscriptions (the limit); unlisten some first."),
    };

    /// <summary>A message's wire type for logs ("query", "write", …).</summary>
    private static string MessageType(ClientMessage message)
    {
        var name = message.GetType().Name;
        return name.EndsWith("Message", StringComparison.Ordinal) ? name[..^"Message".Length] : name;
    }

    private ServerMessage HandleListen(ConnectionScope scope, WsConnection conn, string id, Query query, long? resumeToken, long? clientProtocol,
        ExpandRequest? expand)
    {
        if (scope.AtSubscriptionLimit) return TooManySubscriptions(scope, id);
        var listener = scope.Db.Listen(query, resumeToken is { } seq ? new ListenOptions(ResumeFrom: seq) : null);
        var subscriptionId = Guid.NewGuid().ToString("N");
        var cts = CancellationTokenSource.CreateLinkedTokenSource(conn.Closed);
        var supportsDeleted = clientProtocol >= SubscriptionPump.DeletedKindMinProtocol;
        var pump = SubscriptionPump.RunQueryAsync(subscriptionId, listener, scope, conn, supportsDeleted, _logger, cts.Token,
            expand is null ? null : new LiveExpansion(scope.Db, expand));
        return Register(scope, subscriptionId, listener, pump, cts, id);
    }

    private async Task<ServerMessage> HandleDocListen(
        ConnectionScope scope, WsConnection conn, string id, string path, long? resumeToken, long? clientProtocol, ExpandRequest? expand, CancellationToken ct)
    {
        if (scope.AtSubscriptionLimit) return TooManySubscriptions(scope, id);
        var options = resumeToken is { } seq ? new ListenOptions(ResumeFrom: seq) : null;
        var listener = await scope.Db.ListenToDocumentAsync(path, options, ct);   // get-authorized; throws → mapped to error frame
        var subscriptionId = Guid.NewGuid().ToString("N");
        var cts = CancellationTokenSource.CreateLinkedTokenSource(conn.Closed);
        var supportsDeleted = clientProtocol >= SubscriptionPump.DeletedKindMinProtocol;
        var pump = SubscriptionPump.RunDocumentAsync(subscriptionId, listener, scope, conn, supportsDeleted, _logger, cts.Token,
            expand is null ? null : new LiveExpansion(scope.Db, expand));
        return Register(scope, subscriptionId, listener, pump, cts, id);
    }

    private static ServerMessage Register(
        ConnectionScope scope, string subscriptionId, IAsyncDisposable listener, Task pump, CancellationTokenSource cts, string id)
    {
        lock (scope.Gate) scope.Subscriptions[subscriptionId] = new ConnectionScope.Subscription(listener, pump, cts);
        Winche.Database.Runtime.WincheDatabaseMetrics.WsSubscriptions.Add(1);
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
            Winche.Database.Runtime.WincheDatabaseMetrics.WsSubscriptions.Add(-1);
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
