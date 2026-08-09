using System.Diagnostics;
using System.Globalization;
using Npgsql;
using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Planning;
using Winche.Database.Querying.Sql;
using Winche.Database.Runtime;
using Winche.Database.Values;

namespace Winche.Database.Querying;

/// <summary>
/// The query path end-to-end: normalize → compile → execute → decode. When <paramref name="statementTimeout"/>
/// is set and no external <paramref name="tx"/> was supplied, each statement runs inside a transaction this
/// class opens and commits itself, purely so a per-statement <c>SET LOCAL statement_timeout</c> can be
/// applied — Postgres reverts a <c>LOCAL</c> setting when the transaction ends, so it never outlives the
/// single statement it was meant for, regardless of what the underlying pooled connection is reused for next.
/// <para>
/// <paramref name="limits"/> is applied here rather than only at a transport boundary so that no caller —
/// including one added later — can reach the planner with an unbounded query shape.
/// </para>
/// </summary>
public sealed class QueryExecutor(
    NpgsqlConnection conn, NpgsqlTransaction? tx, CollectionIndexResolver? scopes = null, TimeSpan? statementTimeout = null,
    QueryLimits? limits = null)
{
    public async Task<QueryResult> ExecuteAsync(Query query, CancellationToken ct = default)
    {
        var plan = Normalizer.Normalize(query, limits);
        var pageNode = plan.Nodes.OfType<PageNode>().Single();
        var compiled = SqlCompiler.Compile(plan, scopes?.ScopeFor(query.Collection), query.Select);

        var docs = await RunBoundedAsync(async statementTx =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = statementTx;
            compiled.Apply(cmd);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            return await TypedDocumentReader.ReadAllAsync(reader, ct);
        }, ct);

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
        var plan = Normalizer.Normalize(query, limits);
        var compiled = CountSql.Compile(plan, query.Limit, scopes?.ScopeFor(query.Collection));

        return await RunBoundedAsync(async statementTx =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = statementTx;
            compiled.Apply(cmd);

            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }, ct);
    }

    /// <summary>
    /// Runs the given aggregations over the same match as <see cref="CountAsync"/>, in one query.
    /// sum() returns an integer Value when all operands are integers (else double); average()
    /// returns a double Value, or null when no numeric operand matched; count() returns an integer.
    /// </summary>
    public async Task<AggregationResult> AggregateAsync(Query query, IReadOnlyList<Aggregation> aggregations, CancellationToken ct = default)
    {
        AggregateValidator.Validate(aggregations);

        var plan = Normalizer.Normalize(query, limits);
        var compiled = AggregateSql.Compile(plan, aggregations, query.Limit, scopes?.ScopeFor(query.Collection));

        return await RunBoundedAsync(statementTx => ReadAggregateAsync(compiled, aggregations, statementTx, ct), ct);
    }

    private async Task<AggregationResult> ReadAggregateAsync(
        CompiledSql compiled, IReadOnlyList<Aggregation> aggregations, NpgsqlTransaction? statementTx, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = statementTx;
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

    /// <summary>
    /// Runs <paramref name="body"/> against the statement transaction to use (an externally supplied
    /// <see cref="tx"/> takes precedence — this class never nests transactions of its own inside one it
    /// didn't open). With no external transaction and a positive <see cref="statementTimeout"/>, wraps
    /// the single statement in a transaction of its own so a <c>SET LOCAL statement_timeout</c> can be
    /// applied — <c>LOCAL</c> settings are transaction-scoped in Postgres itself, so a statement that
    /// commits, rolls back, or is cancelled always leaves the physical connection's session timeout
    /// exactly where it found it before that connection can be reused for anything else.
    /// </summary>
    private async Task<T> RunBoundedAsync<T>(Func<NpgsqlTransaction?, Task<T>> body, CancellationToken ct)
    {
        if (tx is not null || statementTimeout is not { } timeout || timeout <= TimeSpan.Zero)
            return await body(tx);

        await using var statementTx = await conn.BeginTransactionAsync(ct);
        await using (var setCmd = conn.CreateCommand())
        {
            setCmd.Transaction = statementTx;
            // Milliseconds, computed here (never from caller input) — safe to inline; SET does not accept parameters.
            var ms = (long)Math.Ceiling(timeout.TotalMilliseconds);
            setCmd.CommandText = $"SET LOCAL statement_timeout = {ms.ToString(CultureInfo.InvariantCulture)}";
            await setCmd.ExecuteNonQueryAsync(ct);
        }

        try
        {
            var result = await body(statementTx);
            await statementTx.CommitAsync(ct);
            return result;
        }
        catch (PostgresException ex) when (ex.SqlState == "57014")  // query_canceled (statement_timeout)
        {
            throw new RuntimeException(RuntimeStatus.DeadlineExceeded,
                $"Query exceeded the statement timeout ({timeout}).");
        }
    }
}
