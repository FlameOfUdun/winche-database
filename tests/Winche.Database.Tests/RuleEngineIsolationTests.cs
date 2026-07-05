using Microsoft.Extensions.DependencyInjection;
using Winche.Database.Constants;
using Winche.Database.DependencyInjection;
using Winche.Rules;
using Winche.Rules.Evaluation;
using Winche.Rules.Expressions;

namespace Winche.Database.Tests;

public class RuleEngineIsolationTests
{
    private static ServiceProvider BuildProvider(Action<WincheDatabaseOptions>? extra = null)
    {
        var services = new ServiceCollection();
        services.AddWincheDatabase(o =>
        {
            // A well-formed connection string is enough; nothing connects during resolution.
            o.ConnectionString = "Host=localhost;Port=5432;Database=test;Username=test;Password=test";
            extra?.Invoke(o);
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Keyed_engine_is_built_from_this_packages_own_rules()
    {
        using var provider = BuildProvider(o => o.UseRules(rb =>
            rb.Match("docs/{id}", mb => mb.Allow([RuleOperation.Get], Expr.Const(true)))));

        var engine = provider.GetRequiredKeyedService<RuleEngine>(WincheDatabaseKeys.RuleEngine);

        Assert.True(await engine.AllowsAsync(RuleOperation.Get, "docs/1", new RuleRequest()));
        Assert.False(await engine.AllowsAsync(RuleOperation.Get, "files/1", new RuleRequest()));
    }

    [Fact]
    public async Task With_no_UseRules_access_is_default_deny()
    {
        using var provider = BuildProvider();

        var engine = provider.GetRequiredKeyedService<RuleEngine>(WincheDatabaseKeys.RuleEngine);

        Assert.False(await engine.AllowsAsync(RuleOperation.Get, "docs/1", new RuleRequest()));
    }

    [Fact]
    public async Task Rules_can_be_hot_swapped_at_runtime_via_the_mutable_repository()
    {
        // Start with no rules → default-deny.
        using var provider = BuildProvider();

        // The engine and its rule-set repository are registered under the same public key. The engine
        // reads the repository's Current ruleset on every evaluation, so replacing it takes effect
        // immediately — no engine rebuild, no container rebuild.
        var engine = provider.GetRequiredKeyedService<RuleEngine>(WincheDatabaseKeys.RuleEngine);
        var rules = provider.GetRequiredKeyedService<IMutableRuleSetRepository>(WincheDatabaseKeys.RuleEngine);

        // Before the swap: denied.
        Assert.False(await engine.AllowsAsync(RuleOperation.Get, "docs/1", new RuleRequest()));

        // Hot-swap in a ruleset that grants Get on docs/{id}.
        rules.Update(RuleSetBuilder.Build(rb =>
            rb.Match("docs/{id}", mb => mb.Allow([RuleOperation.Get], Expr.Const(true)))));

        // The very same engine instance now allows it — it observed the new ruleset.
        Assert.True(await engine.AllowsAsync(RuleOperation.Get, "docs/1", new RuleRequest()));

        // Swap back to an empty (deny-all) ruleset — also observed immediately.
        rules.Update(new RuleSet([]));
        Assert.False(await engine.AllowsAsync(RuleOperation.Get, "docs/1", new RuleRequest()));
    }
}
