using WincheDb.Core.Models;
using WincheDb.DocumentStore.Models;

namespace WincheDb.Realtime.Messages;

public abstract record ServerMessage
{
    public abstract string Type { get; }
}

public record SystemPongResponse : ServerMessage
{
    public override string Type => "system:pong";
    public required string RequestId { get; init; }
}

public record SystemErrorResponse : ServerMessage
{
    public override string Type => "system:error";
    public required string? RequestId { get; init; }
    public required string Message { get; init; }
    public string? Code { get; init; }
}

#region  Document Responses

public record DocumentGetResponse : ServerMessage
{
    public override string Type => "document:get";
    public required string RequestId { get; init; }
    public Document? Document { get; init; }
}

public record DocumentSetResponse : ServerMessage
{
    public override string Type => "document:set";
    public required string RequestId { get; init; }
    public required Document Document { get; init; }
}

public record DocumentUpdateResponse : ServerMessage
{
    public override string Type => "document:update";
    public required string RequestId { get; init; }
    public Document? Document { get; init; }
}

public record DocumentDeleteResponse : ServerMessage
{
    public override string Type => "document:delete";
    public required string RequestId { get; init; }
}

#endregion

#region  Server Notifications

public record QueryUpdateNotification : ServerMessage
{
    public override string Type => "query:update";
    public required string SubscriptionId { get; init; }
    public required QueryChange Change { get; init; }
}

#endregion

#region Query Responses

public record QueryExecuteResponse : ServerMessage
{
    public override string Type => "query:execute";
    public required string RequestId { get; init; }
    public required QueryResult Result { get; init; }
}

public record QuerySubscribeResponse : ServerMessage
{
    public override string Type => "query:subscribe";
    public required string RequestId { get; init; }
    public required string SubscriptionId { get; init; }
    public required QueryResult Result { get; init; }
}

public record QueryUnsubscribeResponse : ServerMessage
{
    public override string Type => "query:unsubscribe";
    public required string RequestId { get; init; }
    public required string SubscriptionId { get; init; }
}

#endregion

#region Transaction Responses

public record TransactionBeginResponse : ServerMessage
{
    public override string Type => "transaction:begin";
    public required string RequestId { get; init; }
    public required string TransactionId { get; init; }
}

public record TransactionGetResponse : DocumentGetResponse
{
    public override string Type => "transaction:get";
    public required string TransactionId { get; init; }
}
public record TransactionSetResponse : DocumentSetResponse
{
    public override string Type => "transaction:set";
    public required string TransactionId { get; init; }
}

public record TransactionUpdateResponse : DocumentUpdateResponse
{
    public override string Type => "transaction:update";
    public required string TransactionId { get; init; }
}

public record TransactionDeleteResponse : DocumentDeleteResponse
{
    public override string Type => "transaction:delete";
    public required string TransactionId { get; init; }
}

public record TransactionQueryResponse : QueryExecuteResponse
{
    public override string Type => "transaction:query";
    public required string TransactionId { get; init; }
}

public record TransactionCommitResponse : ServerMessage
{
    public override string Type => "transaction:commit";
    public required string RequestId { get; init; }
    public required string TransactionId { get; init; }
}

public record TransactionRollbackResponse : ServerMessage
{
    public override string Type => "transaction:rollback";
    public required string RequestId { get; init; }
    public required string TransactionId { get; init; }
    public required string Reason { get; init; }
}

#endregion

#region Batch Responses

public record BatchCommitResponse : ServerMessage
{
    public override string Type => "batch:commit";
    public required string RequestId { get; init; }
    public required List<Document?> Documents { get; init; }
}

#endregion

#region Sync Responses

public record SyncPushResponse : ServerMessage
{
    public override string Type => "sync:push";
    public required string RequestId { get; init; }
    public required string Path { get; init; }
    public Document? Document { get; init; }
    public required int AppliedCount { get; init; }
    public required bool HasConflict { get; init; }
}

#endregion
