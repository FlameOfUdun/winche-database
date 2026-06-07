using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Winche.Database.Models;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Ast.Serialization;
using Winche.Database.Values;

namespace Winche.Database.AspNetCore.WebSockets.Messages;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SystemPingRequest), "system:ping")]
[JsonDerivedType(typeof(DocumentGetRequest), "document:get")]
[JsonDerivedType(typeof(DocumentSetRequest), "document:set")]
[JsonDerivedType(typeof(DocumentUpdateRequest), "document:update")]
[JsonDerivedType(typeof(DocumentDeleteRequest), "document:delete")]
[JsonDerivedType(typeof(QueryExecuteRequest), "query:execute")]
[JsonDerivedType(typeof(QuerySubscribeRequest), "query:subscribe")]
[JsonDerivedType(typeof(QueryUnsubscribeRequest), "query:unsubscribe")]
[JsonDerivedType(typeof(TransactionBeginRequest), "transaction:begin")]
[JsonDerivedType(typeof(TransactionGetRequest), "transaction:get")]
[JsonDerivedType(typeof(TransactionSetRequest), "transaction:set")]
[JsonDerivedType(typeof(TransactionUpdateRequest), "transaction:update")]
[JsonDerivedType(typeof(TransactionDeleteRequest), "transaction:delete")]
[JsonDerivedType(typeof(TransactionQueryRequest), "transaction:query")]
[JsonDerivedType(typeof(TransactionCommitRequest), "transaction:commit")]
[JsonDerivedType(typeof(TransactionRollbackRequest), "transaction:rollback")]
[JsonDerivedType(typeof(BatchCommitRequest), "batch:commit")]
[JsonDerivedType(typeof(SyncPushRequest), "sync:push")]
[JsonDerivedType(typeof(AggregateExecuteRequest), "aggregate:execute")]
public abstract record ClientMessage
{
    [Required]
    [JsonPropertyName("id")]
    public required string Id { get; init; }
}

public record SystemPingRequest : ClientMessage;

public record DocumentGetRequest : ClientMessage
{
    [Required]
    [JsonPropertyName("path")]
    public required string Path { get; init; }
}
public record DocumentDeleteRequest : ClientMessage
{
    [Required]
    [JsonPropertyName("path")]
    public required string Path { get; init; }
}
public record DocumentSetRequest : ClientMessage
{
    [Required]
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [Required]
    [JsonPropertyName("data")]
    [JsonConverter(typeof(FieldsJsonConverter))]
    public required IReadOnlyDictionary<string, Value> Data { get; init; }
}
public record DocumentUpdateRequest : ClientMessage
{
    [Required]
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [Required]
    [JsonPropertyName("data")]
    [JsonConverter(typeof(FieldsJsonConverter))]
    public required IReadOnlyDictionary<string, Value> Data { get; init; }
}


public record QueryExecuteRequest : ClientMessage
{
    [Required]
    [JsonPropertyName("query")]
    public QueryAst Query { get; init; } = null!;
}
public record QuerySubscribeRequest : ClientMessage
{
    [Required]
    [JsonPropertyName("query")]
    public QueryAst Query { get; init; } = null!;
}
public record QueryUnsubscribeRequest : ClientMessage
{
    [Required]
    [JsonPropertyName("subscriptionId")]
    public required string SubscriptionId { get; init; }
}

public record TransactionBeginRequest : ClientMessage;
public record TransactionCommitRequest : ClientMessage
{
    [Required]
    [JsonPropertyName("transactionId")]
    public required string TransactionId { get; init; }
}
public record TransactionRollbackRequest : ClientMessage
{
    [Required]
    [JsonPropertyName("transactionId")]
    public required string TransactionId { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}
public record TransactionGetRequest : ClientMessage
{
    [Required]
    [JsonPropertyName("transactionId")]
    public required string TransactionId { get; init; }

    [Required]
    [JsonPropertyName("path")]
    public required string Path { get; init; }
}
public record TransactionDeleteRequest : ClientMessage
{
    [Required]
    [JsonPropertyName("transactionId")]
    public required string TransactionId { get; init; }

    [Required]
    [JsonPropertyName("path")]
    public required string Path { get; init; }
}
public record TransactionSetRequest : ClientMessage
{
    [Required]
    [JsonPropertyName("transactionId")]
    public required string TransactionId { get; init; }

    [Required]
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [Required]
    [JsonPropertyName("data")]
    [JsonConverter(typeof(FieldsJsonConverter))]
    public required IReadOnlyDictionary<string, Value> Data { get; init; }
}
public record TransactionUpdateRequest : ClientMessage
{
    [Required]
    [JsonPropertyName("transactionId")]
    public required string TransactionId { get; init; }

    [Required]
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [Required]
    [JsonPropertyName("data")]
    [JsonConverter(typeof(FieldsJsonConverter))]
    public required IReadOnlyDictionary<string, Value> Data { get; init; }
}
public record TransactionQueryRequest : ClientMessage
{
    [Required]
    [JsonPropertyName("transactionId")]
    public required string TransactionId { get; init; }

    [Required]
    [JsonPropertyName("query")]
    public QueryAst Query { get; init; } = null!;
}

public record BatchCommitRequest : ClientMessage
{
    [Required]
    [JsonPropertyName("batch")]
    public required OperationBatch Batch { get; init; }
}

public record SyncPushRequest : ClientMessage
{
    [Required]
    [JsonPropertyName("batch")]
    public required MutationBatch Batch { get; init; }
}

public record AggregateExecuteRequest : ClientMessage
{
    [Required]
    [JsonPropertyName("pipeline")]
    public PipelineAst Pipeline { get; init; } = null!;
}
