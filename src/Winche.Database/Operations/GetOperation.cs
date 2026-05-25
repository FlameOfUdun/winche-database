using Npgsql;
using Winche.Database.Core.Infrastructure;
using Winche.Database.Core.Models;
using Winche.Database.Infrastructure;
using Winche.Database.SQL.OperationBuilders;

namespace Winche.Database.Operations;

internal sealed class GetOperation
{
    private readonly NpgsqlConnection _conn;
    private readonly NpgsqlTransaction? _tx;
    private readonly string _table;

    internal GetOperation(NpgsqlConnection conn, NpgsqlTransaction? tx, string table)
    {
        _conn = conn;
        _tx = tx;
        _table = table;
    }


    internal async Task<Document?> ExecuteAsync(string path, CancellationToken ct)
    {
        if (!DocumentPathParser.IsValidDocumentPath(path, out var error))
            throw new ArgumentException(error);

        await using var cmd = _conn.CreateCommand();
        cmd.Transaction = _tx;

        var result = new GetSqlBuilder(_table).Build(path);
        result.Apply(cmd);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await NpgsqlDocumentReader.ReadSingleAsync(reader, ct);
    }
}