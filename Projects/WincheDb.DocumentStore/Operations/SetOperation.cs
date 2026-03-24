using Npgsql;
using System.Text.Json.Nodes;
using WincheDb.Core.Infrastructure;
using WincheDb.Core.Models;
using WincheDb.DocumentStore.Infrastructure;
using WincheDb.SqlBuilder;

namespace WincheDb.DocumentStore.Operations;

internal sealed class SetOperation
{
    private readonly NpgsqlConnection _conn;
    private readonly NpgsqlTransaction? _tx;
    private readonly string _table;

    internal SetOperation(NpgsqlConnection conn, NpgsqlTransaction? tx, string table)
    {
        _conn = conn;
        _tx = tx;
        _table = table;
    }

    internal async Task<Document> ExecuteAsync(string path, JsonObject data, CancellationToken ct)
    {
        if (!DocumentPathParser.IsValidDocumentPath(path, out var error))
            throw new ArgumentException(error);

        await using var cmd = _conn.CreateCommand();
        cmd.Transaction = _tx;

        var info = DocumentPathParser.ParsePath(path);
        var result = new SetSqlBuilder(_table).Build(info.Id!, info.Collection, data);
        result.Apply(cmd);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var doc = await NpgsqlDocumentReader.ReadSingleAsync(reader, ct);

        return doc 
            ?? throw new InvalidOperationException("Failed to insert document");
    }
}