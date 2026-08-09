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

    // ── Authorization runs before preconditions can leak status ────────────────

    private sealed class DenyAllAccess : IWriteAuthorizer
    {
        public Task AuthorizeAsync(IReadOnlyList<PendingWrite> w, ITransactionalDocumentReader r, DateTimeOffset t, CancellationToken ct)
            => throw new AccessDeniedException(w[0].Write.Path, "write");
    }

    private sealed class DenyPath(string path) : IWriteAuthorizer
    {
        public Task AuthorizeAsync(IReadOnlyList<PendingWrite> w, ITransactionalDocumentReader r, DateTimeOffset t, CancellationToken ct)
        {
            if (w.Any(pw => pw.Write.Path == path))
                throw new AccessDeniedException(path, "write");
            return Task.CompletedTask;
        }
    }

    private sealed class AllowAll : IWriteAuthorizer
    {
        public Task AuthorizeAsync(IReadOnlyList<PendingWrite> w, ITransactionalDocumentReader r, DateTimeOffset t, CancellationToken ct)
            => Task.CompletedTask;
    }

    private static Dictionary<FieldPath, Value> UField(string k, Value v) => new() { [FieldPath.Parse(k)] = v };

    [Fact]
    public async Task Update_Unauthorized_ExistingAndMissingDocument_BothDeniedNoDistinction()
    {
        var existing = $"things/{Guid.NewGuid():N}";
        var missing = $"things/{Guid.NewGuid():N}";

        var seedApplier = new WriteApplier(Fx.DataSource);
        await seedApplier.ApplyAsync([new SetWrite { Path = existing, Fields = new Dictionary<string, Value> { ["x"] = new IntegerValue(1) } }]);

        var applier = new WriteApplier(Fx.DataSource, new DenyAllAccess());

        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            applier.ApplyAsync([new UpdateWrite { Path = existing, Fields = UField("x", new IntegerValue(2)) }]));

        // Unauthorized caller cannot distinguish "exists" from "missing" via the resulting status.
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            applier.ApplyAsync([new UpdateWrite { Path = missing, Fields = UField("x", new IntegerValue(2)) }]));
    }

    [Fact]
    public async Task Create_Unauthorized_ExistsFalsePrecondition_ExistingAndMissing_BothDeniedNoAlreadyExistsLeak()
    {
        var existing = $"things/{Guid.NewGuid():N}";
        var missing = $"things/{Guid.NewGuid():N}";

        var seedApplier = new WriteApplier(Fx.DataSource);
        await seedApplier.ApplyAsync([new SetWrite { Path = existing, Fields = new Dictionary<string, Value> { ["x"] = new IntegerValue(1) } }]);

        var applier = new WriteApplier(Fx.DataSource, new DenyAllAccess());

        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            applier.ApplyAsync([new SetWrite { Path = existing, Fields = new Dictionary<string, Value>(), Precondition = new Precondition(Exists: false) }]));

        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            applier.ApplyAsync([new SetWrite { Path = missing, Fields = new Dictionary<string, Value>(), Precondition = new Precondition(Exists: false) }]));
    }

    [Fact]
    public async Task Update_Unauthorized_UpdateTimePreconditionMismatch_NoFailedPreconditionLeak()
    {
        var path = $"things/{Guid.NewGuid():N}";
        var seedApplier = new WriteApplier(Fx.DataSource);
        await seedApplier.ApplyAsync([new SetWrite { Path = path, Fields = new Dictionary<string, Value> { ["x"] = new IntegerValue(1) } }]);

        var applier = new WriteApplier(Fx.DataSource, new DenyAllAccess());
        var staleTime = DateTimeOffset.UtcNow.AddDays(-1);

        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            applier.ApplyAsync([new SetWrite
            {
                Path = path,
                Fields = new Dictionary<string, Value>(),
                Precondition = new Precondition(UpdateTime: staleTime),
            }]));
    }

    [Fact]
    public async Task Authorized_StillGetsRealPreconditionStatusesAndMessages()
    {
        var existing = $"things/{Guid.NewGuid():N}";
        var missing = $"things/{Guid.NewGuid():N}";

        var seedApplier = new WriteApplier(Fx.DataSource);
        await seedApplier.ApplyAsync([new SetWrite { Path = existing, Fields = new Dictionary<string, Value> { ["x"] = new IntegerValue(1) } }]);

        var applier = new WriteApplier(Fx.DataSource, new AllowAll());

        // NOT_FOUND: update against a missing document.
        var notFound = await Assert.ThrowsAsync<RuntimeException>(() =>
            applier.ApplyAsync([new UpdateWrite { Path = missing, Fields = UField("x", new IntegerValue(1)) }]));
        Assert.Equal(RuntimeStatus.NotFound, notFound.Status);
        Assert.Contains(missing, notFound.Message);

        // ALREADY_EXISTS: exists:false precondition against an existing document.
        var alreadyExists = await Assert.ThrowsAsync<RuntimeException>(() =>
            applier.ApplyAsync([new SetWrite
            {
                Path = existing,
                Fields = new Dictionary<string, Value>(),
                Precondition = new Precondition(Exists: false),
            }]));
        Assert.Equal(RuntimeStatus.AlreadyExists, alreadyExists.Status);
        Assert.Contains(existing, alreadyExists.Message);

        // FAILED_PRECONDITION: stale updateTime precondition.
        var failedPrecondition = await Assert.ThrowsAsync<RuntimeException>(() =>
            applier.ApplyAsync([new SetWrite
            {
                Path = existing,
                Fields = new Dictionary<string, Value>(),
                Precondition = new Precondition(UpdateTime: DateTimeOffset.UtcNow.AddDays(-1)),
            }]));
        Assert.Equal(RuntimeStatus.FailedPrecondition, failedPrecondition.Status);
        Assert.Contains(existing, failedPrecondition.Message);

        // The document was left untouched by every failed attempt above.
        var db = new DocumentDatabase(Fx.DataSource, Options.Create(new WincheDatabaseOptions()));
        Assert.Equal(new IntegerValue(1), (await db.GetAsync(existing))!.Fields["x"]);
    }

    [Fact]
    public async Task Batch_EarlyPreconditionFailure_LaterWriteUnauthorized_YieldsPermissionDenied()
    {
        var existing = $"things/{Guid.NewGuid():N}";
        var unauthorized = $"things/{Guid.NewGuid():N}";

        var seedApplier = new WriteApplier(Fx.DataSource);
        await seedApplier.ApplyAsync([new SetWrite { Path = existing, Fields = new Dictionary<string, Value> { ["x"] = new IntegerValue(1) } }]);

        var applier = new WriteApplier(Fx.DataSource, new DenyPath(unauthorized));

        // First write fails FAILED_PRECONDITION (stale updateTime); second write would be
        // fine on its own but is denied by the authorizer. The denial must win.
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            applier.ApplyAsync(
            [
                new SetWrite
                {
                    Path = existing,
                    Fields = new Dictionary<string, Value>(),
                    Precondition = new Precondition(UpdateTime: DateTimeOffset.UtcNow.AddDays(-1)),
                },
                new SetWrite { Path = unauthorized, Fields = new Dictionary<string, Value> { ["y"] = new IntegerValue(1) } },
            ]));

        var db = new DocumentDatabase(Fx.DataSource, Options.Create(new WincheDatabaseOptions()));
        Assert.Null(await db.GetAsync(unauthorized));
        Assert.Equal(new IntegerValue(1), (await db.GetAsync(existing))!.Fields["x"]);
    }
}
