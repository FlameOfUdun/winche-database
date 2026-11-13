using Microsoft.Extensions.DependencyInjection;
using Winche.Database.Authorization;
using Winche.Database.DependencyInjection;
using Winche.Database.Runtime;
using Winche.Database.Runtime.History;
using Winche.Database.Runtime.Writes;
using Winche.Values;
using Winche.Rules;
using Winche.Rules.Expressions;

namespace Winche.Database.IntegrationTests;

/// <summary>Document history (KeepHistory, 11.1.0): before/after and who, read back with DocumentHistory.</summary>
[Collection("postgres")]
public class HistoryTests(PostgresFixture fx) : IAsyncLifetime
{
    public Task InitializeAsync() => fx.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class Claims : IRuleClaimsAccessor
    {
        public IReadOnlyDictionary<string, object?>? Value { get; set; }
        public IReadOnlyDictionary<string, object?>? GetClaims() => Value;
    }

    private ServiceProvider Provider(bool history, out Claims claims)
    {
        var c = claims = new Claims();
        var services = new ServiceCollection();
        services.AddWincheDatabase(o =>
        {
            o.ConnectionString = fx.ConnectionString;
            o.UseRules(RuleSetBuilder.Build(r => r.Match("{document=**}", b => b.Allow(RuleOperations.All, Expr.Const(true)))));
            if (history) o.KeepHistory(TimeSpan.FromDays(14));
        });
        services.AddSingleton<IRuleClaimsAccessor>(c);
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, Value> Fields(params (string K, Value V)[] f) => f.ToDictionary(x => x.K, x => x.V);

    [Fact]
    public async Task Changes_record_before_after_and_who()
    {
        await using var sp = Provider(history: true, out var claims);
        var db = sp.GetRequiredService<IDocumentDatabase>();       // the rules-guarded one, as the app uses it
        var history = sp.GetRequiredService<DocumentHistory>();

        claims.Value = new Dictionary<string, object?> { ["uid"] = "u1" };
        await db.WriteAsync([new SetWrite { Path = "users/u1", Fields = Fields(("name", new StringValue("Ann")), ("age", new IntegerValue(30))) }]);
        claims.Value = new Dictionary<string, object?> { ["sub"] = "u2" };                    // sub when there's no uid
        await db.WriteAsync([new SetWrite { Path = "users/u1", Fields = Fields(("name", new StringValue("Annie")), ("phone", new StringValue("123"))) }]);
        claims.Value = null;
        using (DocumentWriteActor.Use("console:admin@example.com"))                            // explicit beats claims
            await db.WriteAsync([new DeleteWrite { Path = "users/u1" }]);
        await db.WriteAsync([new SetWrite { Path = "users/u2", Fields = Fields(("name", new StringValue("Bo"))) }]);   // no one
        await db.WriteAsync([new SetWrite { Path = "teams/t1", Fields = Fields(("name", new StringValue("Red"))) }]);

        var u1 = await history.QueryAsync(new HistoryQuery { Path = "users/u1" });
        Assert.Equal(["removed", "modified", "added"], u1.Select(e => e.Type));
        Assert.Equal(["console:admin@example.com", "u2", "u1"], u1.Select(e => e.Actor));

        var added = u1[2];
        Assert.Null(added.Before);
        Assert.Equal(["added:age", "added:name"], added.Fields!.Select(f => $"{f.Kind}:{f.Field}"));

        var modified = u1[1];
        Assert.Equal(["removed:age", "changed:name", "added:phone"], modified.Fields!.Select(f => $"{f.Kind}:{f.Field}"));
        var name = modified.Fields!.Single(f => f.Field == "name");
        Assert.Equal(new StringValue("Ann"), name.Before);
        Assert.Equal(new StringValue("Annie"), name.After);
        Assert.Equal(1, modified.Before!.Version);
        Assert.Equal(2, modified.After!.Version);

        var removed = u1[0];
        Assert.Null(removed.After);
        Assert.Equal(["removed:name", "removed:phone"], removed.Fields!.Select(f => $"{f.Kind}:{f.Field}"));

        // Everything under users (not teams), by an actor, and paged.
        var users = await history.QueryAsync(new HistoryQuery { Under = "users" });
        Assert.Equal(4, users.Count);
        Assert.All(users, e => Assert.StartsWith("users/", e.Path));
        Assert.Null(users[0].Actor);                                                           // users/u2 by no one
        Assert.Equal(["users/u1"], (await history.QueryAsync(new HistoryQuery { Actor = "u2" })).Select(e => e.Path));
        var page1 = await history.QueryAsync(new HistoryQuery { Limit = 2 });
        var page2 = await history.QueryAsync(new HistoryQuery { Limit = 2, Before = page1[^1].Seq });
        Assert.Equal(5, page1.Count + page2.Count + (await history.QueryAsync(new HistoryQuery { Limit = 2, Before = page2[^1].Seq })).Count);
        Assert.True(page1[^1].Seq > page2[0].Seq);
    }

    [Fact]
    public async Task Without_history_changes_carry_no_snapshots_or_actor()
    {
        await using var sp = Provider(history: false, out var claims);
        var db = sp.GetRequiredService<IDocumentDatabase>();
        claims.Value = new Dictionary<string, object?> { ["uid"] = "u1" };
        await db.WriteAsync([new SetWrite { Path = "users/u1", Fields = Fields(("name", new StringValue("Ann"))) }]);

        var entry = Assert.Single(await sp.GetRequiredService<DocumentHistory>().QueryAsync(new HistoryQuery { Path = "users/u1" }));
        Assert.Equal("added", entry.Type);
        Assert.Null(entry.Actor);
        Assert.Null(entry.After);
        Assert.Null(entry.Fields);   // unknown: recorded before history was on
    }

    [Fact]
    public void Diff_lists_top_level_changes_in_name_order()
    {
        var diff = DocumentHistory.Diff(
            Fields(("a", new IntegerValue(1)), ("b", new StringValue("x")), ("m", new MapValue(Fields(("k", new IntegerValue(1)))))),
            Fields(("b", new StringValue("x")), ("c", new BooleanValue(true)), ("m", new MapValue(Fields(("k", new IntegerValue(2)))))));
        Assert.Equal(["removed:a", "added:c", "changed:m"], diff.Select(f => $"{f.Kind}:{f.Field}"));
    }

    [Fact]
    public void The_actor_scope_restores_the_previous_one()
    {
        Assert.Null(DocumentWriteActor.Current);
        using (DocumentWriteActor.Use("a"))
        {
            using (DocumentWriteActor.Use("b")) Assert.Equal("b", DocumentWriteActor.Current);
            Assert.Equal("a", DocumentWriteActor.Current);
        }
        Assert.Null(DocumentWriteActor.Current);
    }
}
