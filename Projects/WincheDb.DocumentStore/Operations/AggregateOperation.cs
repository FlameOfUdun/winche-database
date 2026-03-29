using Npgsql;
using WincheDb.Core.Ast;
using WincheDb.DocumentStore.Infrastructure;
using WincheDb.DocumentStore.Models;
using WincheDb.SqlBuilder.AggSqlBuilders;

namespace WincheDb.DocumentStore.Operations;

internal sealed class AggregateOperation
{
    private readonly NpgsqlConnection _conn;
    private readonly NpgsqlTransaction? _tx;
    private readonly string _table;

    internal AggregateOperation(NpgsqlConnection conn, NpgsqlTransaction? tx, string table)
    {
        _conn = conn;
        _tx = tx;
        _table = table;
    }

    internal async Task<AggregateResult> ExecuteAsync(List<PipelineStage> pipeline, CancellationToken ct)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.Transaction = _tx;

        var result = AggregatePipelineBuilder.Build(pipeline, _table);
        result.Apply(cmd);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await NpgsqlAggregateReader.ReadAsync(reader, ct);
    }
}