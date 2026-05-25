using Npgsql;
using System.Text.Json.Nodes;
using Winche.Database.Core.Models;

namespace Winche.Database.Infrastructure;

internal static class NpgsqlDocumentReader
{
    private static Document FromReader(NpgsqlDataReader reader)
    {
        var path = reader.GetString(reader.GetOrdinal("path"));
        var id = reader.GetString(reader.GetOrdinal("id"));
        var collection = reader.GetString(reader.GetOrdinal("collection"));
        var data = reader.GetString(reader.GetOrdinal("data"));
        var createdAt = reader.GetFieldValue<DateTime>(reader.GetOrdinal("created_at"));
        var updatedAt = reader.GetFieldValue<DateTime>(reader.GetOrdinal("updated_at"));
        var version = reader.GetInt64(reader.GetOrdinal("version"));

        return new Document
        {
            Id = id,
            Path = path,
            Collection = collection,
            CreatedAt = createdAt.ToUniversalTime(),
            UpdatedAt = updatedAt.ToUniversalTime(),
            Version = version,
            Data = JsonNode.Parse(data)?.AsObject() ?? []
        };
    }

    internal static async Task<Document?> ReadSingleAsync(NpgsqlDataReader reader, CancellationToken ct)
    {
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }
        return FromReader(reader);
    }

    internal static async Task<List<Document>> ReadAllAsync(NpgsqlDataReader reader, CancellationToken ct)
    {
        var docs = new List<Document>();
        while (await reader.ReadAsync(ct))
        {
            docs.Add(FromReader(reader));
        }
        return docs;
    }
}