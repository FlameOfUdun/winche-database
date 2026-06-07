using Npgsql;
using Winche.Database.Core.Infrastructure;
using Winche.Database.Querying.Sql;
using Winche.Database.Values;

namespace Winche.Database.Documents;

/// <summary>
/// Typed single-document CRUD against an open connection (and optional transaction).
/// The new engine's execution primitive — DocumentManager adopts it in Phase 4.
/// </summary>
public sealed class DocumentOperations(NpgsqlConnection conn, NpgsqlTransaction? tx, string table)
{
    public async Task<Document?> GetAsync(string path, CancellationToken ct = default)
    {
        ValidateDocumentPath(path);
        await using var cmd = CreateCommand(DocumentSql.Get(table, path));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await TypedDocumentReader.ReadSingleAsync(reader, ct);
    }

    public async Task<Document> SetAsync(string path, IReadOnlyDictionary<string, Value> fields, CancellationToken ct = default)
    {
        ValidateDocumentPath(path);
        var info = DocumentPathParser.ParsePath(path);
        if (string.IsNullOrEmpty(info.Id))
            throw new ArgumentException($"Path '{path}' does not contain a document id.");

        await using var cmd = CreateCommand(DocumentSql.Upsert(table, path, info.Id, info.Collection, StorageCodec.Encode(fields)));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await TypedDocumentReader.ReadSingleAsync(reader, ct)
            ?? throw new InvalidOperationException($"Upsert returned no row for '{path}'.");
    }

    public async Task<Document?> UpdateAsync(string path, IReadOnlyDictionary<string, Value> patch, CancellationToken ct = default)
    {
        ValidateDocumentPath(path);

        var ownsTx = tx is null;
        var transaction = tx ?? await conn.BeginTransactionAsync(ct);
        try
        {
            Document? current;
            await using (var getCmd = CreateCommand(DocumentSql.Get(table, path, forUpdate: true), transaction))
            await using (var reader = await getCmd.ExecuteReaderAsync(ct))
            {
                current = await TypedDocumentReader.ReadSingleAsync(reader, ct);
            }

            if (current is null)
            {
                if (ownsTx) await transaction.RollbackAsync(ct);
                return null;
            }

            var merged = DocumentMerger.Merge(current.Fields, patch);

            Document? updated;
            await using (var updateCmd = CreateCommand(DocumentSql.UpdateData(table, path, StorageCodec.Encode(merged)), transaction))
            await using (var reader = await updateCmd.ExecuteReaderAsync(ct))
            {
                updated = await TypedDocumentReader.ReadSingleAsync(reader, ct);
            }

            if (ownsTx) await transaction.CommitAsync(ct);
            return updated;
        }
        catch
        {
            if (ownsTx) await transaction.RollbackAsync(ct);
            throw;
        }
        finally
        {
            if (ownsTx) await transaction.DisposeAsync();
        }
    }

    public async Task<IReadOnlyList<string>> DeleteAsync(string path, CancellationToken ct = default)
    {
        if (!DocumentPathParser.IsValidPath(path, out var error))
            throw new ArgumentException(error);

        await using var cmd = CreateCommand(DocumentSql.DeleteSubtree(table, path));
        var deleted = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            deleted.Add(reader.GetString(0));
        return deleted;
    }

    /// <summary>Locks the path's subtree rows (FOR UPDATE) and returns their paths — used by authorized cascade delete.</summary>
    public async Task<IReadOnlyList<string>> SelectForUpdateAsync(string path, CancellationToken ct = default)
    {
        if (!DocumentPathParser.IsValidPath(path, out var error))
            throw new ArgumentException(error);

        await using var cmd = CreateCommand(DocumentSql.SelectSubtreeForUpdate(table, path));
        var paths = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            paths.Add(reader.GetString(0));
        return paths;
    }

    private NpgsqlCommand CreateCommand(CompiledSql sql, NpgsqlTransaction? explicitTx = null)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = explicitTx ?? tx;
        return sql.Apply(cmd);
    }

    private static void ValidateDocumentPath(string path)
    {
        if (!DocumentPathParser.IsValidDocumentPath(path, out var error))
            throw new ArgumentException(error);
    }
}
