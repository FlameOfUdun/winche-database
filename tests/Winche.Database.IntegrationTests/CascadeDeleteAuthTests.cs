using Microsoft.Extensions.Options;
using Winche.Database.Authorization;
using Winche.Database.DependencyInjection;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;
using Winche.Rules;
using Winche.Rules.Expressions;

namespace Winche.Database.IntegrationTests;

/// <summary>
/// A cascade delete removes every document beneath its root, so each one must pass the delete rule for its
/// own path — not only the root. Rules: the owner may delete their root and anything beneath it except the
/// read-only <c>audit</c> collection.
/// </summary>
[Collection("postgres")]
public class CascadeDeleteAuthTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private static readonly RuleSet Rules = RuleSetBuilder.Build(r =>
    {
        var owner = Expr.Param("uid").Eq(Expr.Auth("uid"));
        r.Match("users/{uid}", b => b.Allow(RuleOperations.Of(RuleOperation.Get, RuleOperation.Delete), owner));
        r.Match("users/{uid}/{coll}/{doc}", b => b.Allow(
            RuleOperations.Of(RuleOperation.Delete),
            owner.And(Expr.Param("coll").In(Expr.Value(RuleValue.List([RuleValue.String("audit")]))).Not())));
    });

    private DocumentDatabase Core() => new(Fx.DataSource, Options.Create(new WincheDatabaseOptions()));

    private DocumentDatabase As(string uid) =>
        new(Fx.DataSource, Options.Create(new WincheDatabaseOptions()),
            writeAuthorizer: new RulesWriteAuthorizer(
                new RuleEngine(new StaticRuleSetRepository(Rules), WincheRuleValueComparer.Instance),
                new StaticClaimsAccessor(new Dictionary<string, object?> { ["uid"] = uid })));

    private static SetWrite Doc(string path) => new() { Path = path, Fields = new Dictionary<string, Value> { ["x"] = new IntegerValue(1) } };

    [Fact]
    public async Task Cascade_IsDenied_WhenADescendantCannotBeDeleted_AndNothingIsRemoved()
    {
        var uid = $"u{Guid.NewGuid():N}";
        await Core().WriteAsync([Doc($"users/{uid}"), Doc($"users/{uid}/notes/n1"), Doc($"users/{uid}/audit/a1")]);

        var ex = await Assert.ThrowsAsync<AccessDeniedException>(() =>
            As(uid).WriteAsync([new DeleteWrite { Path = $"users/{uid}", Cascade = true }]));

        Assert.Contains($"users/{uid}/audit/a1", ex.Message);
        Assert.NotNull(await Core().GetAsync($"users/{uid}"));
        Assert.NotNull(await Core().GetAsync($"users/{uid}/notes/n1"));
        Assert.NotNull(await Core().GetAsync($"users/{uid}/audit/a1"));
    }

    [Fact]
    public async Task Cascade_Succeeds_WhenEveryDescendantMayBeDeleted()
    {
        var uid = $"u{Guid.NewGuid():N}";
        await Core().WriteAsync([Doc($"users/{uid}"), Doc($"users/{uid}/notes/n1"), Doc($"users/{uid}/notes/n2")]);

        await As(uid).WriteAsync([new DeleteWrite { Path = $"users/{uid}", Cascade = true }]);

        Assert.Null(await Core().GetAsync($"users/{uid}"));
        Assert.Null(await Core().GetAsync($"users/{uid}/notes/n1"));
        Assert.Null(await Core().GetAsync($"users/{uid}/notes/n2"));
    }

    [Fact]
    public async Task Cascade_ByAnotherUser_IsDenied_AtTheRoot()
    {
        var uid = $"u{Guid.NewGuid():N}";
        await Core().WriteAsync([Doc($"users/{uid}"), Doc($"users/{uid}/notes/n1")]);

        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            As("someone-else").WriteAsync([new DeleteWrite { Path = $"users/{uid}", Cascade = true }]));
        Assert.NotNull(await Core().GetAsync($"users/{uid}/notes/n1"));
    }

    [Fact]
    public async Task TrustedServerCascade_IsNotRuleChecked()
    {
        var uid = $"u{Guid.NewGuid():N}";
        await Core().WriteAsync([Doc($"users/{uid}"), Doc($"users/{uid}/audit/a1")]);

        await Core().WriteAsync([new DeleteWrite { Path = $"users/{uid}", Cascade = true }]);

        Assert.Null(await Core().GetAsync($"users/{uid}/audit/a1"));
    }
}
