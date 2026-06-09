using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Winche.Database.Querying.Ast;

namespace Winche.Database.AspNetCore.WebSockets.Protocol;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(PingMessage), "ping")]
[JsonDerivedType(typeof(AuthRefreshMessage), "auth.refresh")]
[JsonDerivedType(typeof(DocGetMessage), "doc.get")]
[JsonDerivedType(typeof(DocGetAllMessage), "doc.getAll")]
[JsonDerivedType(typeof(QueryMessage), "query")]
[JsonDerivedType(typeof(CountMessage), "count")]
[JsonDerivedType(typeof(AggregateMessage), "aggregate")]
[JsonDerivedType(typeof(WriteMessage), "write")]
[JsonDerivedType(typeof(TxBeginMessage), "tx.begin")]
[JsonDerivedType(typeof(TxGetMessage), "tx.get")]
[JsonDerivedType(typeof(TxQueryMessage), "tx.query")]
[JsonDerivedType(typeof(TxCommitMessage), "tx.commit")]
[JsonDerivedType(typeof(TxRollbackMessage), "tx.rollback")]
[JsonDerivedType(typeof(ListenMessage), "listen")]
[JsonDerivedType(typeof(UnlistenMessage), "unlisten")]
public abstract record ClientMessage
{
    [JsonPropertyName("id")] public string? Id { get; init; }
}

public sealed record HelloMessage : ClientMessage
{
    [JsonPropertyName("protocol")] public int Protocol { get; init; }
    [JsonPropertyName("token")] public string? Token { get; init; }
}

public sealed record PingMessage : ClientMessage;

public sealed record AuthRefreshMessage : ClientMessage
{
    [JsonPropertyName("token")] public required string Token { get; init; }
}

public sealed record DocGetMessage : ClientMessage
{
    [JsonPropertyName("path")] public required string Path { get; init; }
}

public sealed record DocGetAllMessage : ClientMessage
{
    [JsonPropertyName("paths")] public required IReadOnlyList<string> Paths { get; init; }
}

public sealed record QueryMessage : ClientMessage
{
    [JsonPropertyName("query")] public required Query Query { get; init; }
}

public sealed record CountMessage : ClientMessage
{
    [JsonPropertyName("query")] public required Query Query { get; init; }
}

public sealed record AggregateMessage : ClientMessage
{
    [JsonPropertyName("pipeline")] public required Pipeline Pipeline { get; init; }
}

/// <summary>writes stay raw JSON here; WriteWireParser owns the Write wire format.</summary>
public sealed record WriteMessage : ClientMessage
{
    [JsonPropertyName("writes")] public required JsonArray Writes { get; init; }
}

public sealed record TxBeginMessage : ClientMessage;

public sealed record TxGetMessage : ClientMessage
{
    [JsonPropertyName("transactionId")] public required string TransactionId { get; init; }
    [JsonPropertyName("path")] public required string Path { get; init; }
}

public sealed record TxQueryMessage : ClientMessage
{
    [JsonPropertyName("transactionId")] public required string TransactionId { get; init; }
    [JsonPropertyName("query")] public required Query Query { get; init; }
}

public sealed record TxCommitMessage : ClientMessage
{
    [JsonPropertyName("transactionId")] public required string TransactionId { get; init; }
    [JsonPropertyName("writes")] public required JsonArray Writes { get; init; }
}

public sealed record TxRollbackMessage : ClientMessage
{
    [JsonPropertyName("transactionId")] public required string TransactionId { get; init; }
}

public sealed record ListenMessage : ClientMessage
{
    [JsonPropertyName("query")] public required Query Query { get; init; }
    [JsonPropertyName("resumeToken")] public long? ResumeToken { get; init; }
}

public sealed record UnlistenMessage : ClientMessage
{
    [JsonPropertyName("subscriptionId")] public required string SubscriptionId { get; init; }
}
