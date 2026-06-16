using Microsoft.Extensions.Options;
using Winche.Database.Authorization;
using Winche.Database.DependencyInjection;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;
using Winche.Rules;
using Winche.Rules.Expressions;
using Xunit;

namespace Winche.Database.IntegrationTests;

/// <summary>
/// Verifies that transactional reads (<c>tx:get</c>) are authorized by the guard, closing the gap
/// where <see cref="RuleGuardedDocumentDatabase.GetAsync(string,string,System.Threading.CancellationToken)"/>
/// previously delegated to the core without a rule check.
/// </summary>
[Collection("postgres")]
public class TransactionalGetAuthTests(PostgresFixture fx) : QueryTestBase(fx)
{
    private DocumentDatabase Core() => new(Fx.DataSource, Options.Create(new WincheDatabaseOptions()));

    private static IReadOnlyDictionary<string, object?> Claims => new Dictionary<string, object?> { ["uid"] = "u" };

    [Fact]
    public async Task TxGet_DeniedByRules_Throws()
    {
        var core = Core();
        var path = $"secret/{Guid.NewGuid():N}";
        await core.WriteAsync([new SetWrite { Path = path, Fields = new Dictionary<string, Value> { ["x"] = new IntegerValue(1) } }]);

        // Deny-all ruleset: a transactional get must now throw rather than leak the document.
        var guard = new RuleGuardedDocumentDatabase(core, new RuleEngine(RulesetBuilder.Build(_ => { }), WincheRuleValueComparer.Instance), new StaticClaimsAccessor(Claims));

        var tx = await guard.BeginTransactionAsync();
        await Assert.ThrowsAsync<AccessDeniedException>(() => guard.GetAsync(tx.Id, path));
    }

    [Fact]
    public async Task TxGet_AllowedByRules_ReturnsDocument()
    {
        var core = Core();
        var path = $"secret/{Guid.NewGuid():N}";
        await core.WriteAsync([new SetWrite { Path = path, Fields = new Dictionary<string, Value> { ["x"] = new IntegerValue(1) } }]);

        var allowRead = RulesetBuilder.Build(r =>
            r.Match("secret/{id}", m => m.Allow(RuleOperations.Read, new LiteralExpression(RuleValue.Bool(true)))));
        var guard = new RuleGuardedDocumentDatabase(core, new RuleEngine(allowRead, WincheRuleValueComparer.Instance), new StaticClaimsAccessor(Claims));

        var tx = await guard.BeginTransactionAsync();
        var doc = await guard.GetAsync(tx.Id, path);

        Assert.NotNull(doc);
        Assert.Equal(new IntegerValue(1), doc!.Fields["x"]);
    }
}
