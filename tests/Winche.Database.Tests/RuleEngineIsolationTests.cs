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

        var engine = provider.GetRequiredKeyedService<RuleEngine>(ServiceKeys.RULE_ENGINE_KEY);

        Assert.True(await engine.AllowsAsync(RuleOperation.Get, "docs/1", new RuleRequest()));
        Assert.False(await engine.AllowsAsync(RuleOperation.Get, "files/1", new RuleRequest()));
    }

    [Fact]
    public async Task With_no_UseRules_access_is_default_deny()
    {
        using var provider = BuildProvider();

        var engine = provider.GetRequiredKeyedService<RuleEngine>(ServiceKeys.RULE_ENGINE_KEY);

        Assert.False(await engine.AllowsAsync(RuleOperation.Get, "docs/1", new RuleRequest()));
    }
}
