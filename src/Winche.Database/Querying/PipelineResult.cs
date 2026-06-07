// src/Winche.Database/Querying/PipelineResult.cs
using System.Text.Json.Serialization;
using Winche.Database.Values;

namespace Winche.Database.Querying;

/// <summary>Typed pipeline output. Absent keys = missing fields (SQL NULL columns).</summary>
public sealed record PipelineResult(
    [property: JsonPropertyName("rows")] IReadOnlyList<IReadOnlyDictionary<string, Value>> Rows);
