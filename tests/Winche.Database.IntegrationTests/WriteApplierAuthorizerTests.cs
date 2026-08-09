using Microsoft.Extensions.Options;
using Winche.Database.Authorization;
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

    private sealed class RecordingAuthorizer : IWriteAuthorizer
    {
        public readonly List<string> AuthorizedPaths = [];
        public Task AuthorizeAsync(IReadOnlyList<PendingWrite> w, ITransactionalDocumentReader r, DateTimeOffset t, CancellationToken ct)
        {
            AuthorizedPaths.AddRange(w.Select(pw => pw.Write.Path));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task CascadeDelete_AuthorizesRootAndEachDescendantExactlyOnce()
    {
        var seedApplier = new WriteApplier(Fx.DataSource);
        var root = $"orgs/{Guid.NewGuid():N}";
        await seedApplier.ApplyAsync(
        [
            new SetWrite { Path = root, Fields = new Dictionary<string, Value> { ["x"] = new IntegerValue(1) } },
            new SetWrite { Path = $"{root}/teams/t1", Fields = new Dictionary<string, Value> { ["x"] = new IntegerValue(1) } },
            new SetWrite { Path = $"{root}/teams/t2", Fields = new Dictionary<string, Value> { ["x"] = new IntegerValue(1) } },
        ]);

        var recorder = new RecordingAuthorizer();
        var applier = new WriteApplier(Fx.DataSource, recorder);
        await applier.ApplyAsync([new DeleteWrite { Path = root, Cascade = true }]);

        Assert.Equal(3, recorder.AuthorizedPaths.Count);
        Assert.Equal(recorder.AuthorizedPaths.Count, recorder.AuthorizedPaths.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(root, recorder.AuthorizedPaths);
        Assert.Contains($"{root}/teams/t1", recorder.AuthorizedPaths);
        Assert.Contains($"{root}/teams/t2", recorder.AuthorizedPaths);
    }

    [Fact]
    public async Task CascadeDelete_RootWithNoDocumentOfItsOwn_DoesNotThrow()
    {
        var seedApplier = new WriteApplier(Fx.DataSource);
        var root = $"orgs/{Guid.NewGuid():N}";
        // The root itself is never written — only a descendant document exists.
        await seedApplier.ApplyAsync(
        [
            new SetWrite { Path = $"{root}/teams/t1", Fields = new Dictionary<string, Value> { ["x"] = new IntegerValue(1) } },
        ]);

        var recorder = new RecordingAuthorizer();
        var applier = new WriteApplier(Fx.DataSource, recorder);
        await applier.ApplyAsync([new DeleteWrite { Path = root, Cascade = true }]);

        Assert.Equal(2, recorder.AuthorizedPaths.Count);   // root (no doc) + the one descendant
        Assert.Contains(root, recorder.AuthorizedPaths);
        Assert.Contains($"{root}/teams/t1", recorder.AuthorizedPaths);

        var db = new DocumentDatabase(Fx.DataSource, Options.Create(new WincheDatabaseOptions()));
        Assert.Null(await db.GetAsync($"{root}/teams/t1"));
    }
}
