using Microsoft.Extensions.Options;
using Winche.Database.DependencyInjection;
using Winche.Database.Documents;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;
using Xunit;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class WriteApplierAuthorizerTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private sealed class DenyAll : IWriteAuthorizer
    {
        public Task AuthorizeAsync(IReadOnlyList<PendingWrite> w, ITransactionalDocumentReader r, DateTimeOffset t, CancellationToken ct)
            => throw new RuntimeException(RuntimeStatus.Aborted, "denied");
    }

    private sealed class CaptureAfter : IWriteAuthorizer
    {
        public Document? After;
        public Task AuthorizeAsync(IReadOnlyList<PendingWrite> w, ITransactionalDocumentReader r, DateTimeOffset t, CancellationToken ct)
        { After = w[0].After; return Task.CompletedTask; }
    }

    [Fact]
    public async Task Deny_RollsBackWrite()
    {
        var applier = new WriteApplier(Fx.DataSource, new DenyAll());
        var path = $"things/{Guid.NewGuid():N}";
        await Assert.ThrowsAsync<RuntimeException>(() =>
            applier.ApplyAsync([new SetWrite { Path = path, Fields = new Dictionary<string, Value> { ["x"] = new IntegerValue(1) } }]));

        var db = new DocumentDatabase(Fx.DataSource, Options.Create(new WincheDatabaseOptions()));
        Assert.Null(await db.GetAsync(path));
    }

    [Fact]
    public async Task After_ReflectsPostWriteState()
    {
        var capture = new CaptureAfter();
        var applier = new WriteApplier(Fx.DataSource, capture);
        var path = $"things/{Guid.NewGuid():N}";
        await applier.ApplyAsync([new SetWrite { Path = path, Fields = new Dictionary<string, Value> { ["x"] = new IntegerValue(7) } }]);

        Assert.NotNull(capture.After);
        Assert.Equal(new IntegerValue(7), capture.After!.Fields["x"]);
    }
}
