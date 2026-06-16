using Npgsql;
using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Planning;
using Winche.Database.Querying.Sql;

namespace Winche.Database.Querying;

/// <summary>The query path end-to-end: normalize → compile → execute → decode.</summary>
public sealed class QueryExecutor(NpgsqlConnection conn, NpgsqlTransaction? tx, CollectionIndexResolver? scopes = null)
{
    public async Task<QueryResult> ExecuteAsync(Query query, CancellationToken ct = default)
    {
        var plan = Normalizer.Normalize(query);
        var limit = plan.Nodes.OfType<PageNode>().Single().Limit;
        var compiled = SqlCompiler.Compile(plan, scopes?.ScopeFor(query.Collection), query.Select);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        compiled.Apply(cmd);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var docs = await TypedDocumentReader.ReadAllAsync(reader, ct);

        var hasMore = docs.Count > limit;
        var page = hasMore ? docs.Take(limit).ToList() : docs;

        return new QueryResult(page, hasMore);
    }

    /// <summary>
    /// COUNT(*) over the same match as <see cref="ExecuteAsync"/>. An explicit <see cref="Query.Limit"/>
    /// caps the count (Firestore semantics); an absent limit counts the full match — the Normalizer's
    /// default page size does NOT apply.
    /// </summary>
    public async Task<long> CountAsync(Query query, CancellationToken ct = default)
    {
        var plan = Normalizer.Normalize(query);
        var compiled = CountSql.Compile(plan, query.Limit, scopes?.ScopeFor(query.Collection));

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        compiled.Apply(cmd);

        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }
}
