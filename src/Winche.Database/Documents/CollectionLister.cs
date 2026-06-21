using Npgsql;
using Winche.Database.Querying.Sql;

namespace Winche.Database.Documents;

/// <summary>
/// Executes the ListCollectionIds descendant scan against an open connection
/// (and optional transaction). Returns the raw, ordered collection ids — paging
/// (page-size clamp, fetch-extra-row, token) is applied by the caller.
/// </summary>
public sealed class CollectionLister(NpgsqlConnection conn, NpgsqlTransaction? tx)
{
    public async Task<IReadOnlyList<string>> ListAsync(
        string? parentDocumentPath, string? afterCollectionId, int limit, CancellationToken ct = default)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        CollectionListSql.Build(parentDocumentPath, afterCollectionId, limit).Apply(cmd);

        var ids = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            ids.Add(reader.GetString(0));
        return ids;
    }
}
