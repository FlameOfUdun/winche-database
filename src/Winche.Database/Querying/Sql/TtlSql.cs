using Winche.Database.Constants;
using Winche.Database.Runtime.Ttl;

namespace Winche.Database.Querying.Sql;

/// <summary>
/// Builds the "select expired document paths" query for a TTL policy (one collection group).
/// The <c>collection_id =</c> predicate is index-backed; the per-row JSONB timestamp cast is not.
/// For a background sweeper (batched, amortised across ticks) that is fine; operators with very large
/// collections may add a functional index on the timestamp path if a per-tick scan becomes hot.
/// </summary>
internal static class TtlSql
{
    public static CompiledSql SelectExpired(TtlPolicy policy, int batchSize)
    {
        var bag = new ParameterBag();
        var collId = bag.Add(policy.CollectionId);
        var tagged = FieldAccessSql.Tagged(policy.Field, bag, "d");   // parameterized JSONB accessor → tagged value
        var limit = bag.Add(batchSize);

        // A missing field or non-timestamp value yields SQL NULL → NULL < now() is not true → excluded.
        var sql =
            $"SELECT d.document_path FROM {WincheTables.Documents} d " +
            $"WHERE d.collection_id = {collId} " +
            $"AND ({tagged} ->> 'timestampValue')::timestamptz < now() " +
            $"LIMIT {limit}";

        return new CompiledSql(sql, bag.ToArray());
    }
}
