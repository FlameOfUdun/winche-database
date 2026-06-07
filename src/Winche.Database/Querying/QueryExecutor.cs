using Npgsql;
using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Planning;
using Winche.Database.Querying.Sql;

namespace Winche.Database.Querying;

/// <summary>The query path end-to-end: normalize → compile → execute → decode.</summary>
public sealed class QueryExecutor(NpgsqlConnection conn, NpgsqlTransaction? tx)
{
    public async Task<QueryResult> ExecuteAsync(QueryAst query, CancellationToken ct = default)
    {
        var plan = Normalizer.Normalize(query);
        var limit = plan.Nodes.OfType<PageNode>().Single().Limit;
        var compiled = SqlCompiler.Compile(plan);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        compiled.Apply(cmd);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var docs = await TypedDocumentReader.ReadAllAsync(reader, ct);

        var hasMore = docs.Count > limit;
        return new QueryResult(hasMore ? docs.Take(limit).ToList() : docs, hasMore);
    }
}
