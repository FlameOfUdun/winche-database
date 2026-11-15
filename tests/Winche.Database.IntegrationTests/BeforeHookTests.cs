using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Winche.Database.Abstraction;
using Winche.Database.Authorization;
using Winche.Database.DependencyInjection;
using Winche.Database.Documents;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Writes;
using Winche.Values;

namespace Winche.Database.IntegrationTests;

/// <summary>Before hooks: in the write's transaction, after the rules; they refuse or change what's saved.</summary>
[Collection("postgres")]
public class BeforeHookTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private sealed class NoEditsAfterSignOff : DocumentBeforeHook
    {
        public override Task OnBeforeWriteAsync(DocumentWrite write, CancellationToken ct)
        {
            if (write.Before?.Fields.GetValueOrDefault("signedOff") is BooleanValue { Value: true })
                write.Deny(write.Operation == WriteOperation.Delete ? "Signed-off records can't be deleted." : "This record is signed off.");
            return Task.CompletedTask;
        }
    }

    private sealed class Stamp : DocumentBeforeHook
    {
        public List<(string Path, WriteOperation Op)> Seen { get; } = [];
        public override Task OnBeforeWriteAsync(DocumentWrite write, CancellationToken ct)
        {
            lock (Seen) Seen.Add((write.Path, write.Operation));
            if (write.Operation == WriteOperation.Delete) return Task.CompletedTask;
            write.Set("updatedBy", write.Uid ?? "server").Remove("draftNote");
            if (write.Get("title") is StringValue t) write.Set("meta.slug", t.Value.ToLowerInvariant().Replace(' ', '-'));
            return Task.CompletedTask;
        }
    }

    private DocumentDatabase Db(string? uid, params BeforeHookRegistration[] hooks) =>
        new(Fx.DataSource, Options.Create(new WincheDatabaseOptions()), null, null, null, null,
            new StaticClaimsAccessor(uid is null ? null : new Dictionary<string, object?> { ["uid"] = uid }))
        { BeforeHooks = new BeforeWriteHooks(hooks, () => uid is null ? null : new Dictionary<string, object?> { ["uid"] = uid }) };

    private static Dictionary<string, Value> F(params (string, object?)[] fields) => fields.ToDictionary(f => f.Item1, f => Value.FromObject(f.Item2));
    private static string P(string what) => $"{what}{Guid.NewGuid():N}";

    [Fact]
    public async Task A_hook_can_refuse_an_update_or_a_delete_and_nothing_is_saved()
    {
        var path = $"records/{P("r")}";
        var db = Db("ann", new BeforeHookRegistration("records/{id}", new NoEditsAfterSignOff()));
        await db.WriteAsync([new SetWrite { Path = path, Fields = F(("n", 1), ("signedOff", true)) }]);

        var update = await Assert.ThrowsAsync<WincheDeniedException>(() => db.WriteAsync([new SetWrite { Path = path, Fields = F(("n", 2)) }]));
        Assert.Equal("This record is signed off.", update.Message);
        var delete = await Assert.ThrowsAsync<WincheDeniedException>(() => db.WriteAsync([new DeleteWrite { Path = path }]));
        Assert.Equal("Signed-off records can't be deleted.", delete.Message);
        Assert.Equal(1L, ((IntegerValue)(await db.GetAsync(path))!.Fields["n"]).Value);

        var error = Winche.Database.Wire.ErrorMapper.Map(update);
        Assert.Equal((WincheErrorCode.PermissionDenied, "denied", "This record is signed off."), (error.Kind, error.Reason, error.Message));
    }

    [Fact]
    public async Task One_refusal_in_a_batch_or_a_cascade_saves_nothing()
    {
        var root = $"wards/{P("w")}";
        var db = Db(null, new BeforeHookRegistration("wards/{w}/records/{id}", new NoEditsAfterSignOff()));
        await db.WriteAsync([
            new SetWrite { Path = root, Fields = F(("n", 1)) },
            new SetWrite { Path = $"{root}/records/a", Fields = F(("n", 1)) },
            new SetWrite { Path = $"{root}/records/b", Fields = F(("n", 1), ("signedOff", true)) },
        ]);
        var other = $"others/{P("o")}";
        await Assert.ThrowsAsync<WincheDeniedException>(() => db.WriteAsync([
            new SetWrite { Path = other, Fields = F(("n", 1)) },
            new SetWrite { Path = $"{root}/records/b", Fields = F(("n", 2)) },
        ]));
        Assert.Null(await db.GetAsync(other));

        // Deleting the ward with everything under it: record b refuses, so nothing goes.
        await Assert.ThrowsAsync<WincheDeniedException>(() => db.WriteAsync([new DeleteWrite { Path = root, Cascade = true }]));
        Assert.NotNull(await db.GetAsync($"{root}/records/a"));
        Assert.NotNull(await db.GetAsync(root));
    }

    [Fact]
    public async Task A_hook_can_change_what_is_saved_and_sees_each_document_once()
    {
        var stamp = new Stamp();
        var db = Db("ann", new BeforeHookRegistration("posts/{id}", stamp));
        var path = $"posts/{P("p")}";
        await db.WriteAsync([
            new SetWrite { Path = path, Fields = F(("title", "Hello World"), ("draftNote", "secret")) },
            new UpdateWrite { Path = path, Fields = new Dictionary<FieldPath, Value> { [FieldPath.Parse("n")] = new IntegerValue(1) } },
        ]);
        var saved = (await db.GetAsync(path))!.Fields;
        Assert.Equal("ann", ((StringValue)saved["updatedBy"]).Value);
        Assert.Equal("hello-world", ((StringValue)((MapValue)saved["meta"]).Fields["slug"]).Value);
        Assert.False(saved.ContainsKey("draftNote"));
        Assert.Equal([(path, WriteOperation.Create)], stamp.Seen);   // two writes, one document: once, as a create

        await Db(null, new BeforeHookRegistration("posts/{id}", stamp)).WriteAsync([new SetWrite { Path = path, Fields = F(("title", "Server Edit")), Merge = true }]);
        Assert.Equal("server", ((StringValue)(await db.GetAsync(path))!.Fields["updatedBy"]).Value);
        Assert.Equal(WriteOperation.Update, stamp.Seen[^1].Op);
        await db.WriteAsync([new DeleteWrite { Path = path }]);
        Assert.Equal(WriteOperation.Delete, stamp.Seen[^1].Op);
    }

    [Fact]
    public async Task Hooks_run_after_the_rules_and_for_the_apps_own_writes()
    {
        var services = new ServiceCollection().AddLogging();
        var stamp = new Stamp();
        services.AddWincheDatabase(o =>
        {
            o.ConnectionString = Fx.ConnectionString;
            o.UseRulesText("match /posts/{id} { allow read, create: if true; }");
            o.UseHooks(h => h.Before("posts/{id}", stamp));
        });
        await using var sp = services.BuildServiceProvider();
        var guarded = sp.GetRequiredService<RuleGuardedDocumentDatabase>();
        var path = $"posts/{P("p")}";
        await guarded.WriteAsync([new SetWrite { Path = path, Fields = F(("title", "A B")) }]);
        await Assert.ThrowsAsync<AccessDeniedException>(() => guarded.WriteAsync([new DeleteWrite { Path = path }]));   // rules refuse first
        Assert.Single(stamp.Seen);

        var core = sp.GetRequiredService<DocumentDatabase>();   // the app's own, unguarded writes
        await core.WriteAsync([new SetWrite { Path = path, Fields = F(("title", "C D")) }]);
        Assert.Equal("c-d", ((StringValue)((MapValue)(await core.GetAsync(path))!.Fields["meta"]).Fields["slug"]).Value);
    }
}
