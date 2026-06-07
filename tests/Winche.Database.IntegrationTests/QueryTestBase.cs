using Winche.Database.Documents;
using Winche.Database.Querying;
using Winche.Database.Querying.Ast;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

public abstract class QueryTestBase(PostgresFixture fx) : IAsyncLifetime
{
    protected readonly PostgresFixture Fx = fx;

    public async Task InitializeAsync() => await Fx.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    protected static FieldPath F(string p) => FieldPath.Parse(p);

    /// <summary>Seed one document with a single field "f" (most suites query a single field).</summary>
    protected Task Seed(string id, Value value) =>
        SeedDoc(id, new Dictionary<string, Value> { ["f"] = value });

    protected async Task SeedDoc(string id, Dictionary<string, Value> fields, string collection = "c")
    {
        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        await new DocumentOperations(conn, null).SetAsync($"{collection}/{id}", fields);
    }

    protected async Task<QueryResult> Run(QueryAst query)
    {
        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        return await new QueryExecutor(conn, null).ExecuteAsync(query);
    }

    protected async Task<List<string>> Ids(QueryAst query) =>
        [.. (await Run(query)).Documents.Select(d => d.Id)];

    /// <summary>Shorthand: query collection "c" filtered on field "f".</summary>
    protected Task<List<string>> Filter(FilterOperator op, Value operand) =>
        Ids(new QueryAst("c", Where: new FieldFilterAst(F("f"), op, operand)));
}
