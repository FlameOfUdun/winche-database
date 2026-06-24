using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Winche.Database.Documents;
using Winche.Database.Querying;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Ast.Serialization;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.AspNetCore.WebSockets.Protocol;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PingMessage), "ping")]
[JsonDerivedType(typeof(DocGetMessage), "doc.get")]
[JsonDerivedType(typeof(DocGetAllMessage), "doc.getAll")]
[JsonDerivedType(typeof(QueryMessage), "query")]
[JsonDerivedType(typeof(CountMessage), "count")]
[JsonDerivedType(typeof(AggregateMessage), "aggregate")]
[JsonDerivedType(typeof(AddMessage), "add")]
[JsonDerivedType(typeof(WriteMessage), "write")]
[JsonDerivedType(typeof(TxBeginMessage), "tx.begin")]
[JsonDerivedType(typeof(TxGetMessage), "tx.get")]
[JsonDerivedType(typeof(TxQueryMessage), "tx.query")]
[JsonDerivedType(typeof(TxCommitMessage), "tx.commit")]
[JsonDerivedType(typeof(TxRollbackMessage), "tx.rollback")]
[JsonDerivedType(typeof(ListenMessage), "listen")]
[JsonDerivedType(typeof(DocListenMessage), "doc.listen")]
[JsonDerivedType(typeof(UnlistenMessage), "unlisten")]
public abstract record ClientMessage
{
    [JsonPropertyName("id")] public string? Id { get; init; }
}

public sealed record PingMessage : ClientMessage;

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
    [JsonPropertyName("query")] public required Query Query { get; init; }

    [JsonPropertyName("aggregations")]
    [JsonConverter(typeof(AggregationListJsonConverter))]
    public required IReadOnlyList<Aggregation> Aggregations { get; init; }
}

/// <summary>Read-only converter: deserializes the wire <c>[{kind,alias,field?}]</c> array into Aggregations.</summary>
public sealed class AggregationListJsonConverter : JsonConverter<IReadOnlyList<Aggregation>>
{
    public override IReadOnlyList<Aggregation> Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader);
        if (node is not JsonArray arr)
            throw new JsonException("'aggregations' must be an array");
        var list = new List<Aggregation>(arr.Count);
        foreach (var el in arr)
        {
            if (el is not JsonObject o)
                throw new JsonException("each aggregation must be an object");
            var kind = (string?)o["kind"] switch
            {
                "count" => AggregateKind.Count,
                "sum" => AggregateKind.Sum,
                "average" => AggregateKind.Average,
                _ => throw new JsonException("aggregation 'kind' must be count|sum|average"),
            };
            var alias = o["alias"] switch
            {
                null => throw new JsonException("aggregation 'alias' is required"),
                JsonValue av when av.TryGetValue<string>(out var a) => a,
                _ => throw new JsonException("aggregation 'alias' must be a string"),
            };
            var fieldStr = (string?)o["field"];
            FieldPath? field;
            try { field = fieldStr is null ? null : FieldPath.Parse(fieldStr); }
            catch (ArgumentException ex) { throw new JsonException(ex.Message, ex); }
            list.Add(new Aggregation(kind, alias, field));
        }
        return list;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<Aggregation> value, JsonSerializerOptions options) =>
        throw new NotSupportedException("AggregationListJsonConverter is read-only.");
}

public sealed record AddMessage : ClientMessage
{
    [JsonPropertyName("collection")] public required string Collection { get; init; }

    [JsonPropertyName("fields")]
    [JsonConverter(typeof(FieldsJsonConverter))]
    public required IReadOnlyDictionary<string, Value> Fields { get; init; }
}

public sealed record WriteMessage : ClientMessage
{
    [JsonPropertyName("writes")] public required IReadOnlyList<Write> Writes { get; init; }
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
    [JsonPropertyName("writes")] public required IReadOnlyList<Write> Writes { get; init; }
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

public sealed record DocListenMessage : ClientMessage
{
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("resumeToken")] public long? ResumeToken { get; init; }
}

public sealed record UnlistenMessage : ClientMessage
{
    [JsonPropertyName("subscriptionId")] public required string SubscriptionId { get; init; }
}
