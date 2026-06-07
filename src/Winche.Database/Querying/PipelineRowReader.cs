// src/Winche.Database/Querying/PipelineRowReader.cs
using System.Text.Json.Nodes;
using Npgsql;
using Winche.Database.Documents;
using Winche.Database.Querying.Sql;
using Winche.Database.Values;

namespace Winche.Database.Querying;

/// <summary>Decodes pipeline rows by final StageSchema: document rows flatten fields + __name__ + extras; projected rows decode each tagged column.</summary>
internal static class PipelineRowReader
{
    internal static async Task<List<IReadOnlyDictionary<string, Value>>> ReadAsync(
        NpgsqlDataReader reader, StageSchema schema, CancellationToken ct)
    {
        var rows = new List<IReadOnlyDictionary<string, Value>>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(schema switch
            {
                DocumentSchema d => ReadDocumentRow(reader, d),
                RowSchema r => ReadProjectedRow(reader, r),
                _ => throw new NotSupportedException($"Unknown schema: {schema.GetType().Name}"),
            });
        }
        return rows;
    }

    private static IReadOnlyDictionary<string, Value> ReadDocumentRow(NpgsqlDataReader reader, DocumentSchema schema)
    {
        var row = StorageCodec.Decode(reader.GetString(reader.GetOrdinal("data")))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        row["__name__"] = new ReferenceValue(reader.GetString(reader.GetOrdinal("path")));
        AddTaggedColumns(reader, schema.Extra.Keys, row);
        return row;
    }

    private static IReadOnlyDictionary<string, Value> ReadProjectedRow(NpgsqlDataReader reader, RowSchema schema)
    {
        var row = new Dictionary<string, Value>();
        AddTaggedColumns(reader, schema.Columns.Keys, row);
        return row;
    }

    private static void AddTaggedColumns(NpgsqlDataReader reader, IEnumerable<string> names, Dictionary<string, Value> row)
    {
        foreach (var name in names)
        {
            var ord = reader.GetOrdinal(name);
            if (!reader.IsDBNull(ord))
                row[name] = ValueSerializer.Read(JsonNode.Parse(reader.GetString(ord))!);
        }
    }
}
