using Winche.Database.DependencyInjection;
using Winche.Rules;

namespace Winche.Database.Tests;

/// <summary>
/// Apps write rule text; test fixtures still build rulesets in C# with Winche.Rules' internal builder
/// (visible to this assembly). This keeps their <c>UseRules(r =&gt; ...)</c> calls working.
/// </summary>
internal static class RuleBuilderTestExtensions
{
    public static WincheDatabaseOptions UseRules(this WincheDatabaseOptions options, Action<RuleSetBuilder> configure) =>
        options.UseRules(RuleSetBuilder.Build(configure));
}
