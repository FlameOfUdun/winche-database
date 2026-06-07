// src/Winche.Database/Querying/PipelineExecutor.cs
using Npgsql;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Planning;
using Winche.Database.Querying.Sql;

namespace Winche.Database.Querying;

/// <summary>The pipeline path end-to-end: normalize → compile → execute → decode.</summary>
public sealed class PipelineExecutor(NpgsqlConnection conn, NpgsqlTransaction? tx, string table)
{
    public async Task<PipelineResult> ExecuteAsync(PipelineAst pipeline, CancellationToken ct = default)
    {
        var plan = PipelineNormalizer.Normalize(pipeline);
        var (compiled, schema) = PipelineCompiler.Compile(plan, table);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        compiled.Apply(cmd);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return new PipelineResult(await PipelineRowReader.ReadAsync(reader, schema, ct));
    }
}
