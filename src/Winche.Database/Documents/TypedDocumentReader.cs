using Npgsql;

namespace Winche.Database.Documents;

internal static class TypedDocumentReader
{
    internal static async Task<Document?> ReadSingleAsync(NpgsqlDataReader reader, CancellationToken ct) =>
        await reader.ReadAsync(ct) ? FromReader(reader) : null;

    internal static async Task<List<Document>> ReadAllAsync(NpgsqlDataReader reader, CancellationToken ct)
    {
        var docs = new List<Document>();
        while (await reader.ReadAsync(ct))
            docs.Add(FromReader(reader));
        return docs;
    }

    private static Document FromReader(NpgsqlDataReader reader) => new()
    {
        Path = reader.GetString(reader.GetOrdinal("path")),
        Id = reader.GetString(reader.GetOrdinal("id")),
        Collection = reader.GetString(reader.GetOrdinal("collection")),
        Fields = StorageCodec.Decode(reader.GetString(reader.GetOrdinal("data"))),
        CreateTime = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
        UpdateTime = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at")),
        Version = reader.GetInt64(reader.GetOrdinal("version")),
    };
}
