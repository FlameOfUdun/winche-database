using Microsoft.Extensions.Options;
using Winche.Database.Authorization;
using Winche.Database.DependencyInjection;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;
using Winche.Rules;
using Xunit;

namespace Winche.Database.IntegrationTests;

[Collection("postgres")]
public class TransactionalWriteAuthTests(PostgresFixture fx) : QueryTestBase(fx)
{
    [Fact]
    public async Task TxCommit_DeniedWrite_RollsBack()
    {
        var ruleset = RulesetBuilder.Build(_ => { });   // deny-all
        var authorizer = new RulesWriteAuthorizer(new RuleEngine(ruleset, WincheRuleValueComparer.Instance), new StaticClaimsAccessor(new Dictionary<string, object?> { ["uid"] = "u" }));
        var core = new DocumentDatabase(Fx.DataSource, Options.Create(new WincheDatabaseOptions()),
            listeners: null, scopes: null, writeAuthorizer: authorizer);

        var path = $"things/{Guid.NewGuid():N}";
        var tx = await core.BeginTransactionAsync();
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            core.CommitTransactionAsync(tx.Id,
                [new SetWrite { Path = path, Fields = new Dictionary<string, Value> { ["x"] = new IntegerValue(1) } }]));

        Assert.Null(await core.GetAsync(path));   // rolled back — transaction bypass closed
    }
}
