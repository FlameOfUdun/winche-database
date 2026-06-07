using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Winche.Database.Documents;

namespace Winche.Database.AspNetCore.WebSockets.Protocol;

/// <summary>
/// Base for all server-to-client frames. Each concrete type carries its own <c>type</c> wire property
/// so that <c>JsonSerializer.Serialize(msg, msg.GetType())</c> (used in the send loop and tests) always
/// produces the correct discriminator without requiring polymorphic base-type serialization.
/// </summary>
public abstract record ServerMessage;

public sealed record WelcomeMessage : ServerMessage
{
    [JsonPropertyName("type")] public string Type { get; } = "welcome";
    [JsonPropertyName("connectionId")] public required string ConnectionId { get; init; }
    [JsonPropertyName("protocol")] public int Protocol { get; init; } = 3;
}

public sealed record ResponseMessage : ServerMessage
{
    [JsonPropertyName("type")] public string Type { get; } = "response";
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("result")] public JsonNode? Result { get; init; }
}

public sealed record ErrorMessage : ServerMessage
{
    [JsonPropertyName("type")] public string Type { get; } = "error";
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }
    [JsonPropertyName("details")] public JsonObject? Details { get; init; }
}

public sealed record ListenSnapshotMessage : ServerMessage
{
    [JsonPropertyName("type")] public string Type { get; } = "listen.snapshot";
    [JsonPropertyName("subscriptionId")] public required string SubscriptionId { get; init; }
    [JsonPropertyName("documents")] public required IReadOnlyList<Document> Documents { get; init; }
    [JsonPropertyName("readTime")] public required DateTimeOffset ReadTime { get; init; }
    [JsonPropertyName("resumeToken")] public required long ResumeToken { get; init; }
}

public sealed record WireChange(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("document")] Document Document,
    [property: JsonPropertyName("oldIndex")] int OldIndex,
    [property: JsonPropertyName("newIndex")] int NewIndex);

public sealed record ListenDeltaMessage : ServerMessage
{
    [JsonPropertyName("type")] public string Type { get; } = "listen.delta";
    [JsonPropertyName("subscriptionId")] public required string SubscriptionId { get; init; }
    [JsonPropertyName("changes")] public required IReadOnlyList<WireChange> Changes { get; init; }
    [JsonPropertyName("count")] public required int Count { get; init; }
    [JsonPropertyName("readTime")] public required DateTimeOffset ReadTime { get; init; }
    [JsonPropertyName("resumeToken")] public required long ResumeToken { get; init; }
}
