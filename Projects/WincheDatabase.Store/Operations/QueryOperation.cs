using Npgsql;
using WincheDatabase.AST.Models;
using WincheDatabase.Core.Infrastructure;
using WincheDatabase.SQL.QuerySqlBuilders;
using WincheDatabase.Store.Infrastructure;
using WincheDatabase.Store.Models;

namespace WincheDatabase.Store.Operations;

internal sealed class QueryOperation
{
    private readonly NpgsqlConnection _conn;
    private readonly NpgsqlTransaction? _tx;
    private readonly string _table;

    internal QueryOperation(NpgsqlConnection conn, NpgsqlTransaction? tx, string table)
    {
        _conn = conn;
        _tx = tx;
        _table = table;
    }

    internal async Task<QueryResult> ExecuteAsync(Query query, CancellationToken ct)
    {
        if (!DocumentPathParser.IsValidCollectionPath(query.Collection, out var error))
            throw new ArgumentException(error);

        await using var cmd = _conn.CreateCommand();
        cmd.Transaction = _tx;

        var result = new QuerySqlBuilder(_table).Build(query);
        result.Apply(cmd);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var documents = await NpgsqlDocumentReader.ReadAllAsync(reader, ct);

        var hasMore = documents.Count > query.Limit;
        if (hasMore)
            documents = documents.Take(query.Limit).ToList();

        return new QueryResult
        {
            Documents = documents,
            HasMore = hasMore,
        };
    }
}