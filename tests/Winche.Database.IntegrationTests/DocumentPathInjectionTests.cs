using Microsoft.Extensions.Options;
using Winche.Database.Constants;
using Winche.Database.DependencyInjection;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.IntegrationTests;

/// <summary>
/// A non-canonical path (e.g. a doubled leading '/') must never reach storage as a distinct row
/// that reads back as the canonical path — that would let a caller authorized to write exactly
/// one document path inject unbounded aliases of it. See DocumentPathParser path validation.
/// </summary>
[Collection("postgres")]
public class DocumentPathInjectionTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private DocumentDatabase Db() => new(Fx.DataSource, Options.Create(new WincheDatabaseOptions()));

    [Fact]
    public async Task Write_ToDoubledLeadingSlashPath_IsRejected_NoAliasRowCreated()
    {
        var db = Db();

        var ex = await Assert.ThrowsAsync<RuntimeException>(() => db.WriteAsync(
            [new SetWrite { Path = "//users/x", Fields = new Dictionary<string, Value> { ["f"] = new IntegerValue(1) } }]));
        Assert.Equal(RuntimeStatus.InvalidArgument, ex.Status);

        // The canonical row must not exist either — the rejected write must not have landed under
        // its normalized form (the exact injection this test guards against).
        Assert.Null(await db.GetAsync("users/x"));

        await using var conn = await Fx.DataSource.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM {WincheDatabaseTables.Documents} WHERE document_path LIKE '%users%x%'";
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        Assert.Equal(0, count);
    }
}
