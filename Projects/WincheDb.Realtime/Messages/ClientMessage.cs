using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WincheDb.Core.Ast;

namespace WincheDb.Realtime.Messages;


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
public abstract record ClientMessage
{
    [Required]
    public required string Id { get; init; }
}

public record SystemPingRequest : ClientMessage;

public record DocumentGetRequest : ClientMessage
{
    [Required]
    public required string Path { get; init; }
}
public record DocumentDeleteRequest : ClientMessage
{
    [Required]
    public required string Path { get; init; }
}
public record DocumentSetRequest : ClientMessage
{
    [Required]
    public required string Path { get; init; }
    [Required]
    public required JsonObject Data { get; init; }
}
public record DocumentUpdateRequest : ClientMessage
{
    [Required]
    public required string Path { get; init; }
    [Required]
    public required JsonObject Data { get; init; }
}


public record QueryExecuteRequest : ClientMessage
{
    [Required]
    public Query Query { get; init; } = null!;
}
public record QuerySubscribeRequest : ClientMessage
{
    [Required]
    public Query Query { get; init; } = null!;
}
public record QueryUnsubscribeRequest : ClientMessage
{
    [Required]
    public required string SubscriptionId { get; init; }
}

public record TransactionBeginRequest : ClientMessage;
public record TransactionCommitRequest : ClientMessage
{
    [Required]
    public required string TransactionId { get; init; }
}
public record TransactionRollbackRequest : ClientMessage
{
    [Required]
    public required string TransactionId { get; init; }
    public string? Reason { get; init; }
}
public record TransactionGetRequest : ClientMessage
{
    [Required]
    public required string TransactionId { get; init; }
    [Required]
    public required string Path { get; init; }
}
public record TransactionDeleteRequest : ClientMessage
{
    [Required]
    public required string TransactionId { get; init; }
    [Required]
    public required string Path { get; init; }
}
public record TransactionSetRequest : ClientMessage
{
    [Required]
    public required string TransactionId { get; init; }
    [Required]
    public required string Path { get; init; }
    [Required]
    public required JsonObject Data { get; init; }
}
public record TransactionUpdateRequest : ClientMessage
{
    [Required]
    public required string TransactionId { get; init; }
    [Required]
    public required string Path { get; init; }
    [Required]
    public required JsonObject Data { get; init; }
}
public record TransactionQueryRequest : ClientMessage
{
    [Required]
    public required string TransactionId { get; init; }
    [Required]
    public Query Query { get; init; } = null!;
}
