using System.Diagnostics;
using Npgsql;
using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Planning;
using Winche.Database.Querying.Sql;
using Winche.Database.Values;

namespace Winche.Database.Querying;

/// <summary>The query path end-to-end: normalize → compile → execute → decode.</summary>
public sealed class QueryExecutor(NpgsqlConnection conn, NpgsqlTransaction? tx, CollectionIndexResolver? scopes = null)
{
    public async Task<QueryResult> ExecuteAsync(Query query, CancellationToken ct = default)
    {
        var plan = Normalizer.Normalize(query);
        var pageNode = plan.Nodes.OfType<PageNode>().Single();
        var compiled = SqlCompiler.Compile(plan, scopes?.ScopeFor(query.Collection), query.Select);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        compiled.Apply(cmd);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var docs = await TypedDocumentReader.ReadAllAsync(reader, ct);

        // hasMore: a row beyond the page exists. For limitToLast (ReverseResult), the query ran
        // reversed, so this means a row exists BEFORE the returned window, not after it.
        var hasMore = docs.Count > pageNode.Limit;
        var page = (hasMore ? docs.Take(pageNode.Limit) : docs).ToList();
        if (pageNode.ReverseResult) page.Reverse();

        return new QueryResult(page, hasMore);
    }

    /// <summary>
    /// COUNT(*) over the same match as <see cref="ExecuteAsync"/>. An explicit <see cref="Query.Limit"/>
    /// caps the count (count() semantics); an absent limit counts the full match — the Normalizer's
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

    /// <summary>
    /// Runs the given aggregations over the same match as <see cref="CountAsync"/>, in one query.
    /// sum() returns an integer Value when all operands are integers (else double); average()
    /// returns a double Value, or null when no numeric operand matched; count() returns an integer.
    /// </summary>
    public async Task<AggregationResult> AggregateAsync(Query query, IReadOnlyList<Aggregation> aggregations, CancellationToken ct = default)
    {
        AggregateValidator.Validate(aggregations);

        var plan = Normalizer.Normalize(query);
        var compiled = AggregateSql.Compile(plan, aggregations, query.Limit, scopes?.ScopeFor(query.Collection));

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        compiled.Apply(cmd);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("Aggregate query returned no row.");

        var values = new Dictionary<string, Value>(aggregations.Count);
        var col = 0;
        foreach (var a in aggregations)
        {
            switch (a.Kind)
            {
                case AggregateKind.Count:
                    values[a.Alias] = new IntegerValue(reader.GetInt64(col));
                    col += 1;
                    break;

                case AggregateKind.Sum:
                {
                    var intSum = reader.GetFieldValue<decimal>(col);
                    var hasDouble = reader.GetBoolean(col + 1);
                    var dblSum = reader.GetDouble(col + 2);
                    values[a.Alias] = hasDouble || intSum < long.MinValue || intSum > long.MaxValue
                        ? new DoubleValue(dblSum)
                        : new IntegerValue((long)intSum);
                    col += 3;
                    break;
                }

                case AggregateKind.Average:
                    // Row is fully buffered after ReadAsync, so synchronous column getters are used throughout.
                    values[a.Alias] = reader.IsDBNull(col)
                        ? new NullValue()
                        : new DoubleValue(reader.GetDouble(col));
                    col += 1;
                    break;
            }
        }

        // The per-aggregation column offsets above must match AggregateSql's emit layout (Count=1, Sum=3, Average=1).
        Debug.Assert(col == reader.FieldCount, "aggregate column read count must match the emitted columns");

        return new AggregationResult(values);
    }
}
