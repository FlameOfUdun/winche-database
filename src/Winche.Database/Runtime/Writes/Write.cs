using System.Text.Json.Serialization;
using Winche.Database.Documents;
using Winche.Database.Values;

namespace Winche.Database.Runtime.Writes;

public abstract record Write
{
    public required string Path { get; init; }
    public Precondition? Precondition { get; init; }
}

/// <summary>Replace the document, or deep-merge into it when Merge=true.</summary>
public sealed record SetWrite : Write
{
    public required IReadOnlyDictionary<string, Value> Fields { get; init; }
    public bool Merge { get; init; }
    public IReadOnlyList<FieldTransform>? Transforms { get; init; }
}

/// <summary>Field-path update with implicit exists:true. Values may be DeleteFieldValue.Instance.</summary>
public sealed record UpdateWrite : Write
{
    public required IReadOnlyDictionary<FieldPath, Value> Fields { get; init; }
    public IReadOnlyList<FieldTransform>? Transforms { get; init; }
}

/// <summary>Delete. Cascade=true also deletes the subtree (Winche extension, explicit opt-in).</summary>
public sealed record DeleteWrite : Write
{
    public bool Cascade { get; init; }
}

/// <summary>Exists and/or UpdateTime (µs-exact) requirement. At least one must be set.</summary>
public sealed record Precondition(bool? Exists = null, DateTimeOffset? UpdateTime = null);

public enum TransformKind { ServerTimestamp, Increment, Maximum, Minimum, ArrayUnion, ArrayRemove }

public sealed record FieldTransform(FieldPath Field, TransformKind Kind, Value? Operand = null);

public sealed record WriteResult(
    [property: JsonPropertyName("updateTime")] DateTimeOffset UpdateTime,
    [property: JsonPropertyName("transformResults")]
    [property: JsonConverter(typeof(FieldPathValueDictionaryJsonConverter))]
    IReadOnlyDictionary<FieldPath, Value>? TransformResults = null);
