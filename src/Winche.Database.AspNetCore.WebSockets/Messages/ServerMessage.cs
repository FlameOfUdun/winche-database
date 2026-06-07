using System.Text.Json.Serialization;
using Winche.Database.Documents;
using Winche.Database.Models;
using Winche.Database.Querying;

namespace Winche.Database.AspNetCore.WebSockets.Messages;

public abstract record ServerMessage
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }
}

public record SystemPongResponse : ServerMessage
{
    public override string Type => "system:pong";

    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }
}

public record SystemErrorResponse : ServerMessage
{
    public override string Type => "system:error";
    public required string? RequestId { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("code")]
    public string? Code { get; init; }
}

#region  Document Responses

public record DocumentGetResponse : ServerMessage
{
    public override string Type => "document:get";
    public required string RequestId { get; init; }

    [JsonPropertyName("document")]
    public Document? Document { get; init; }
}

public record DocumentSetResponse : ServerMessage
{
    public override string Type => "document:set";
    public required string RequestId { get; init; }

    [JsonPropertyName("document")]
    public required Document Document { get; init; }
}

public record DocumentUpdateResponse : ServerMessage
{
    public override string Type => "document:update";
    public required string RequestId { get; init; }

    [JsonPropertyName("document")]
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

    [JsonPropertyName("subscriptionId")]
    public required string SubscriptionId { get; init; }

    [JsonPropertyName("change")]
    public required QueryChange Change { get; init; }
}

#endregion

#region Query Responses

public record QueryExecuteResponse : ServerMessage
{
    public override string Type => "query:execute";
    public required string RequestId { get; init; }

    [JsonPropertyName("result")]
    public required QueryResult Result { get; init; }
}

public record QuerySubscribeResponse : ServerMessage
{
    public override string Type => "query:subscribe";
    public required string RequestId { get; init; }

    [JsonPropertyName("subscriptionId")]
    public required string SubscriptionId { get; init; }

    [JsonPropertyName("result")]
    public required QueryResult Result { get; init; }
}

public record QueryUnsubscribeResponse : ServerMessage
{
    public override string Type => "query:unsubscribe";
    public required string RequestId { get; init; }

    [JsonPropertyName("subscriptionId")]
    public required string SubscriptionId { get; init; }
}

#endregion

#region Transaction Responses

public record TransactionBeginResponse : ServerMessage
{
    public override string Type => "transaction:begin";
    public required string RequestId { get; init; }

    [JsonPropertyName("transactionId")]
    public required string TransactionId { get; init; }
}

public record TransactionGetResponse : DocumentGetResponse
{
    public override string Type => "transaction:get";

    [JsonPropertyName("transactionId")]
    public required string TransactionId { get; init; }
}
public record TransactionSetResponse : DocumentSetResponse
{
    public override string Type => "transaction:set";

    [JsonPropertyName("transactionId")]
    public required string TransactionId { get; init; }
}

public record TransactionUpdateResponse : DocumentUpdateResponse
{
    public override string Type => "transaction:update";

    [JsonPropertyName("transactionId")]
    public required string TransactionId { get; init; }
}

public record TransactionDeleteResponse : DocumentDeleteResponse
{
    public override string Type => "transaction:delete";

    [JsonPropertyName("transactionId")]
    public required string TransactionId { get; init; }
}

public record TransactionQueryResponse : QueryExecuteResponse
{
    public override string Type => "transaction:query";

    [JsonPropertyName("transactionId")]
    public required string TransactionId { get; init; }
}

public record TransactionCommitResponse : ServerMessage
{
    public override string Type => "transaction:commit";
    public required string RequestId { get; init; }

    [JsonPropertyName("transactionId")]
    public required string TransactionId { get; init; }
}

public record TransactionRollbackResponse : ServerMessage
{
    public override string Type => "transaction:rollback";
    public required string RequestId { get; init; }

    [JsonPropertyName("transactionId")]
    public required string TransactionId { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}

#endregion

#region Batch Responses

public record BatchCommitResponse : ServerMessage
{
    public override string Type => "batch:commit";
    public required string RequestId { get; init; }

    [JsonPropertyName("documents")]
    public required CommitResult Result { get; init; }
}

#endregion

#region Sync Responses

public record SyncPushResponse : ServerMessage
{
    public override string Type => "sync:push";
    public required string RequestId { get; init; }

    [JsonPropertyName("result")]
    public required SyncResult Result { get; init; }
}

#endregion

public record AggregateExecuteResponse : ServerMessage
{
    public override string Type => "aggregate:execute";
    public required string RequestId { get; init; }

    [JsonPropertyName("result")]
    public required PipelineResult Result { get; init; }
}
