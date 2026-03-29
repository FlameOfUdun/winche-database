using Npgsql;
using System.Text.Json.Nodes;
using WincheDb.Core.Infrastructure;
using WincheDb.Core.Models;
using WincheDb.DocumentStore.Infrastructure;
using WincheDb.SqlBuilder.OperationBuilders;

namespace WincheDb.DocumentStore.Operations;

internal sealed class UpdateOperation
{
    private readonly NpgsqlConnection _conn;
    private readonly NpgsqlTransaction? _tx;
    private readonly string _table;

    internal UpdateOperation(NpgsqlConnection conn, NpgsqlTransaction? tx, string table)
    {
        _conn = conn;
        _tx = tx;
        _table = table;
    }

    internal async Task<Document?> ExecuteAsync(string path, JsonObject patch, CancellationToken ct)
    {
        if (!DocumentPathParser.IsValidDocumentPath(path, out var error))
            throw new ArgumentException(error);

        var ownsTx = _tx == null;
        var tx = _tx ?? await _conn.BeginTransactionAsync(ct);

        try
        {
            var document = await new GetOperation(_conn, tx, _table).ExecuteAsync(path, ct);
            if (document == null)
                return null;

            var data = DocumentDataMerger.DeepMerge(document.Data, patch);

            await using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;

            var result = new UpdateSqlBuilder(_table).Build(path, data);
            result.Apply(cmd);

            Document? doc;
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                doc = await NpgsqlDocumentReader.ReadSingleAsync(reader, ct);
            }

            if (ownsTx)
                await tx.CommitAsync(ct);

            return doc;
        }
        catch
        {
            if (ownsTx)
                await tx.RollbackAsync(ct);
            throw;
        }
        finally
        {
            if (ownsTx)
                await tx.DisposeAsync();
        }
    }
}