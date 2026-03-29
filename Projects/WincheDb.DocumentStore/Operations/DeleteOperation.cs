using Npgsql;
using WincheDb.Core.Infrastructure;
using WincheDb.SqlBuilder.OperationBuilders;

namespace WincheDb.DocumentStore.Operations;

internal sealed class DeleteOperation
{
    private readonly NpgsqlConnection _conn;
    private readonly NpgsqlTransaction? _tx;
    private readonly string _table;

    internal DeleteOperation(NpgsqlConnection conn, NpgsqlTransaction? tx, string table)
    {
        _conn = conn;
        _tx = tx;
        _table = table;
    }

    internal async Task<bool> ExecuteAsync(string path, CancellationToken ct)
    {
        if (!DocumentPathParser.IsValidDocumentPath(path, out var error))
            throw new ArgumentException(error);

        await using var cmd = _conn.CreateCommand();
        cmd.Transaction = _tx;

        var result = new DeleteSqlBuilder(_table).Build(path);
        result.Apply(cmd);

        var removed = await cmd.ExecuteScalarAsync(ct);
        return removed is not null;
    }
}