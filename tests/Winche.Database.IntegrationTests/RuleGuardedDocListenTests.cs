using Microsoft.Extensions.Options;
using Winche.Database.Authorization;
using Winche.Database.DependencyInjection;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Listening;
using Winche.Rules;
using Winche.Rules.Expressions;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class RuleGuardedDocListenTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private static RuleGuardedDocumentDatabase Guard(DocumentDatabase core, RuleSet ruleset, IReadOnlyDictionary<string, object?>? claims) =>
        new(core, new RuleEngine(ruleset, WincheRuleValueComparer.Instance), new StaticClaimsAccessor(claims));

    private DocumentDatabase CoreWithDocListeners() =>
        new(Fx.DataSource, Options.Create(new WincheDatabaseOptions()), docListeners: new DocumentListenerRegistry(Fx.DataSource));

    private static RuleSet OwnerOnly() => RulesetBuilder.Build(r =>
        r.Match("userData/{userId}", b =>
            b.Allow(RuleOperations.Of(RuleOperation.Get), Expr.Auth("uid").Eq(Expr.Param("userId")))));

    [Fact]
    public async Task DocListen_OwnerOnlyRule_AllowedForOwner()
    {
        var guard = Guard(CoreWithDocListeners(), OwnerOnly(), new Dictionary<string, object?> { ["uid"] = "alice" });
        await using var listener = await guard.ListenToDocumentAsync("userData/alice");
        Assert.NotNull(listener);
    }

    [Fact]
    public async Task DocListen_OwnerOnlyRule_DeniedForOther()
    {
        var guard = Guard(CoreWithDocListeners(), OwnerOnly(), new Dictionary<string, object?> { ["uid"] = "bob" });
        var ex = await Assert.ThrowsAsync<AccessDeniedException>(() => guard.ListenToDocumentAsync("userData/alice"));
        Assert.Equal("get", ex.Operation);
    }
}
