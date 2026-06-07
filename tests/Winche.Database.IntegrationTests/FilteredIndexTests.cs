using Winche.Database.Constants;
using Winche.Database.Documents;
using Winche.Database.Querying.Ast;
using Winche.Database.Querying.Sql;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

internal sealed class OpenOrdersIndex : IndexDefinition
{
    public override string Collection => "fidx";
    public override IReadOnlyList<IndexField> Fields => [new("amount")];
    public override FilterAst? Where =>
        new FieldFilterAst(FieldPath.Parse("status"), FilterOperator.Eq, new StringValue("open"));
}

[Collection("postgres")]
public class FilteredIndexTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private sealed class OpenOrdersIndexNoWhere : IndexDefinition
    {
        public override string Collection => "fidx";
        public override IReadOnlyList<IndexField> Fields => [new("amount")];
        // Where = null (unfiltered)
    }

    [Fact]
    public async Task BuildCreate_WithWhere_Executes_AndNameDiffersFromUnfiltered()
    {
        var filtered = IndexSql.BuildCreate(new OpenOrdersIndex());
        Assert.Contains("status", filtered);
        Assert.Contains("'open'", filtered);

        // Extract names from both DDL strings and assert they differ
        var unfiltered = IndexSql.BuildCreate(new OpenOrdersIndexNoWhere());
        var filteredName = ExtractIndexName(filtered);
        var unfilteredName = ExtractIndexName(unfiltered);
        Assert.NotEqual(filteredName, unfilteredName);

        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = filtered;
        await cmd.ExecuteNonQueryAsync();                                 // DDL is valid
    }

    private static string ExtractIndexName(string ddl)
    {
        // CREATE INDEX IF NOT EXISTS "name" ON ...
        var start = ddl.IndexOf('"') + 1;
        var end = ddl.IndexOf('"', start);
        return ddl[start..end];
    }

    private static readonly DateTimeOffset T0 = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = new(2025, 6, 1, 0, 0, 0, TimeSpan.Zero);

    public static TheoryData<FilterAst> AgreementPredicates => new()
    {
        new FieldFilterAst(FieldPath.Parse("status"), FilterOperator.Eq, new StringValue("o'pen")),
        new FieldFilterAst(FieldPath.Parse("n"), FilterOperator.Gt, new IntegerValue(5)),
        new FieldFilterAst(FieldPath.Parse("d"), FilterOperator.Lte, new DoubleValue(2.5)),
        new FieldFilterAst(FieldPath.Parse("flag"), FilterOperator.Eq, new BooleanValue(true)),
        new UnaryFilterAst(FieldPath.Parse("maybe"), UnaryOp.Exists),
        new UnaryFilterAst(FieldPath.Parse("maybe"), UnaryOp.IsNull),
        new CompositeFilterAst(CompositeOp.And,
        [
            new FieldFilterAst(FieldPath.Parse("n"), FilterOperator.Gte, new IntegerValue(2)),
            new FieldFilterAst(FieldPath.Parse("status"), FilterOperator.Eq, new StringValue("o'pen")),
        ]),
        // Minor 5: timestamp predicate
        new FieldFilterAst(FieldPath.Parse("at"), FilterOperator.Lte, new TimestampValue(T1)),
        // Minor 5: 0.1 double (shortest round-trip string path)
        new FieldFilterAst(FieldPath.Parse("tiny"), FilterOperator.Eq, new DoubleValue(0.1)),
    };

    [Theory]
    [MemberData(nameof(AgreementPredicates))]
    public async Task LiteralPredicate_AgreesWithQueryExecutor(FilterAst predicate)
    {
        // corpus mixing types, nulls, missing fields, quote-bearing strings, timestamps, small doubles
        var docs = new (string Id, Dictionary<string, Value> Fields)[]
        {
            ("a", new() { ["status"] = new StringValue("o'pen"), ["n"] = new IntegerValue(1), ["d"] = new DoubleValue(1.5), ["flag"] = new BooleanValue(true), ["maybe"] = new NullValue(), ["at"] = new TimestampValue(T0), ["tiny"] = new DoubleValue(0.1) }),
            ("b", new() { ["status"] = new StringValue("closed"), ["n"] = new IntegerValue(7), ["d"] = new DoubleValue(2.5), ["flag"] = new BooleanValue(false), ["at"] = new TimestampValue(T1) }),
            ("c", new() { ["status"] = new IntegerValue(1), ["n"] = new DoubleValue(5.5), ["maybe"] = new StringValue("x") }),
            ("d", new() { ["n"] = new IntegerValue(2) }),
        };
        foreach (var (id, fields) in docs)
            await SeedDoc(id, fields, collection: "fagree");

        // engine truth
        var expected = (await Run(new QueryAst("fagree", Where: predicate, Limit: 100)))
            .Documents.Select(x => x.Path).Order().ToList();

        // literal predicate truth — use unqualified accessor (data->...) without table alias
        var sql = IndexPredicateSql.Emit(predicate);
        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT path FROM {WincheTables.Documents} WHERE collection = 'fagree' AND ({sql}) ORDER BY path";
        var actual = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) actual.Add(reader.GetString(0));

        Assert.Equal(expected, actual);
    }
}
