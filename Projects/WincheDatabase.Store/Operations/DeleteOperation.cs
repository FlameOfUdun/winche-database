using Npgsql;
using WincheDatabase.Core.Infrastructure;
using WincheDatabase.SQL.OperationBuilders;

namespace WincheDatabase.Store.Operations;

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

    internal async Task<IReadOnlyList<string>> ExecuteAsync(string path, CancellationToken ct)
    {
        if (!DocumentPathParser.IsValidPath(path, out var error))
            throw new ArgumentException(error);

        await using var cmd = _conn.CreateCommand();
        cmd.Transaction = _tx;

        var result = new DeleteSqlBuilder(_table).Build(path);
        result.Apply(cmd);

        var deleted = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            deleted.Add(reader.GetString(0));

        return deleted;
    }

    internal async Task<IReadOnlyList<string>> SelectForUpdateAsync(string path, CancellationToken ct)
    {
        if (!DocumentPathParser.IsValidPath(path, out var error))
            throw new ArgumentException(error);

        await using var cmd = _conn.CreateCommand();
        cmd.Transaction = _tx;

        var result = new DeleteSqlBuilder(_table).BuildSelectForUpdate(path);
        result.Apply(cmd);

        var paths = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            paths.Add(reader.GetString(0));

        return paths;
    }
}
