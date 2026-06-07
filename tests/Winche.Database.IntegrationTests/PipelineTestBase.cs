using Winche.Database.Querying;
using Winche.Database.Querying.Ast;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

public abstract class PipelineTestBase(PostgresFixture fx) : QueryTestBase(fx)
{
    protected async Task<PipelineResult> RunPipeline(params StageAst[] stages)
    {
        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        return await new PipelineExecutor(conn, null, Fx.Table).ExecuteAsync(new PipelineAst(stages));
    }

    protected static IntegerValue I(long n) => new(n);
    protected static StringValue S(string s) => new(s);
    protected static DoubleValue D(double d) => new(d);
}
