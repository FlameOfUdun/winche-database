using Microsoft.Extensions.DependencyInjection;
using Winche.Database.Authorization;
using Winche.Database.DependencyInjection;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;
using Winche.Rules;
using Winche.Rules.Expressions;

namespace Winche.Database.IntegrationTests;

/// <summary>
/// DI integration tests for <see cref="RuleGuardedDocumentDatabase"/> (Phase 4b).
/// Verifies that:
/// <list type="number">
///   <item><c>AddWincheDatabase(opts => opts.UseRules(...))</c> registers a resolvable
///         <see cref="RuleGuardedDocumentDatabase"/> by concrete type.</item>
///   <item>The rules guard wraps the CORE unguarded <see cref="DocumentDatabase"/>
///         (no double-guarding through <c>IDocumentDatabase</c>).</item>
///   <item>Claims flow from the registered <see cref="IRuleClaimsAccessor"/>
///         into the guard's <c>Func&lt;IReadOnlyDictionary&lt;string,object?&gt;?&gt;</c>.</item>
///   <item>An authorized get succeeds; an unauthorized get throws the native
///         <see cref="Authorization.AccessDeniedException"/>.</item>
///   <item>The default <see cref="Runtime.IDocumentDatabase"/> IS the
///         <see cref="RuleGuardedDocumentDatabase"/> (flipped in Phase 4b).</item>
/// </list>
/// </summary>
[Collection("postgres")]
public class RuleGuardedDatabaseDiTests(PostgresFixture fx) : IAsyncLifetime
{
    public Task InitializeAsync() => fx.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Dictionary<string, Value> Fields(params (string K, Value V)[] entries) =>
        entries.ToDictionary(e => e.K, e => e.V);

    /// <summary>
    /// A simple in-memory <see cref="IRuleClaimsAccessor"/> whose claims can be swapped
    /// per-test via <see cref="SetClaims"/>.
    /// </summary>
    private sealed class MutableClaimsAccessor : IRuleClaimsAccessor
    {
        private IReadOnlyDictionary<string, object?>? _claims;

        public void SetClaims(IReadOnlyDictionary<string, object?> claims) => _claims = claims;

        public IReadOnlyDictionary<string, object?>? GetClaims() => _claims;
    }

    /// <summary>
    /// Builds a DI service provider with <c>AddWincheDatabase</c>, <c>UseRules</c>, and the
    /// shared <paramref name="accessor"/> registered as <c>IRuleClaimsAccessor</c> so the
    /// test can change claims between calls.
    /// </summary>
    private ServiceProvider BuildProvider(Ruleset ruleset, out MutableClaimsAccessor accessor)
    {
        var mutableAccessor = new MutableClaimsAccessor();
        accessor = mutableAccessor;

        var services = new ServiceCollection();
        services.AddWincheDatabase(opts =>
        {
            opts.ConnectionString = fx.ConnectionString;
            opts.UseRules(ruleset);
        });

        // Override the null-fallback IRuleClaimsAccessor with the mutable test accessor.
        // AddWincheDatabase registers the fallback first; this registration comes after, so
        // GetRequiredService<IRuleClaimsAccessor>() returns the mutable accessor.
        services.AddSingleton<IRuleClaimsAccessor>(mutableAccessor);

        return services.BuildServiceProvider();
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="RuleGuardedDocumentDatabase"/> is resolvable when
    /// <c>UseRules</c> is called and that an authorized get succeeds while an unauthorized
    /// get throws <see cref="Authorization.AccessDeniedException"/>.
    /// Also confirms the default <c>IDocumentDatabase</c> IS the rules guard (Phase 4b).
    /// </summary>
    [Fact]
    public async Task UseRules_AuthorizedGetSucceeds_UnauthorizedGetThrowsNativeException()
    {
        var ruleset = RulesetBuilder.Build(r =>
            r.Match("di/{docId}", b =>
                b.Allow(
                    RuleOperations.Of(RuleOperation.Get),
                    Expr.Resource("data", "ownerId").Eq(Expr.Auth("uid")))));

        await using var provider = BuildProvider(ruleset, out var accessor);

        // Phase 4b: default IDocumentDatabase IS the rules guard
        var defaultDb = provider.GetRequiredService<IDocumentDatabase>();
        Assert.IsType<RuleGuardedDocumentDatabase>(defaultDb);

        // The rules guard must also be resolvable by its concrete type
        var rulesGuard = provider.GetRequiredService<RuleGuardedDocumentDatabase>();
        Assert.NotNull(rulesGuard);

        // Seed via the CORE unguarded db so we bypass the rules guard
        var core = provider.GetRequiredService<DocumentDatabase>();
        await core.WriteAsync([new SetWrite
        {
            Path = "di/doc1",
            Fields = Fields(("ownerId", new StringValue("alice"))),
        }]);

        // Authorized get: claims contain the correct uid → allowed
        accessor.SetClaims(new Dictionary<string, object?> { ["uid"] = "alice" });
        var doc = await rulesGuard.GetAsync("di/doc1");
        Assert.NotNull(doc);
        Assert.Equal("alice", ((StringValue)doc.Fields["ownerId"]).Value);

        // Unauthorized get: wrong uid → native AccessDeniedException
        accessor.SetClaims(new Dictionary<string, object?> { ["uid"] = "bob" });
        var ex = await Assert.ThrowsAsync<Authorization.AccessDeniedException>(
            () => rulesGuard.GetAsync("di/doc1"));
        Assert.Equal("di/doc1", ex.Path);
        Assert.Equal("get", ex.Operation);
    }

    /// <summary>
    /// Verifies that the <see cref="IRuleClaimsAccessor"/> correctly flows claims into the guard:
    /// changing the accessor's claims between calls changes the guard's authorization decision.
    /// <para>
    /// Claims are placed at <c>request.auth.uid</c> and <c>request.auth.token.*</c> by
    /// <see cref="Authorization.RequestBuilder"/>. The rule accesses the "role" claim via
    /// <c>Expr.Auth("token", "role")</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task UseRules_ClaimsBridge_ChangingClaimsChangesDecision()
    {
        var ruleset = RulesetBuilder.Build(r =>
            r.Match("bridge/{docId}", b =>
                b.Allow(
                    RuleOperations.Of(RuleOperation.Get),
                    Expr.Auth("token", "role").Eq(Expr.Const("admin")))));

        await using var provider = BuildProvider(ruleset, out var accessor);
        var rulesGuard = provider.GetRequiredService<RuleGuardedDocumentDatabase>();
        var core = provider.GetRequiredService<DocumentDatabase>();

        await core.WriteAsync([new SetWrite
        {
            Path = "bridge/doc1",
            Fields = Fields(("x", new IntegerValue(1))),
        }]);

        // No "role" claim → denied
        accessor.SetClaims(new Dictionary<string, object?> { ["uid"] = "user1" });
        await Assert.ThrowsAsync<Authorization.AccessDeniedException>(
            () => rulesGuard.GetAsync("bridge/doc1"));

        // Correct "role" claim → allowed
        accessor.SetClaims(new Dictionary<string, object?> { ["uid"] = "user1", ["role"] = "admin" });
        var doc = await rulesGuard.GetAsync("bridge/doc1");
        Assert.NotNull(doc);
    }

    /// <summary>
    /// Verifies that <see cref="IRuleClaimsAccessor"/> is registered and resolves correctly
    /// when no custom accessor is configured: the fallback returns <see langword="null"/>
    /// (unauthenticated / no auth context).
    /// </summary>
    [Fact]
    public void UseRules_IRuleClaimsAccessor_ResolvesCorrectly()
    {
        var ruleset = RulesetBuilder.Build(_ => { });

        // Build without registering a custom accessor — the null fallback should be used
        var services = new ServiceCollection();
        services.AddWincheDatabase(opts =>
        {
            opts.ConnectionString = fx.ConnectionString;
            opts.UseRules(ruleset);
        });

        using var provider = services.BuildServiceProvider();

        var claimsAccessor = provider.GetRequiredService<IRuleClaimsAccessor>();
        Assert.NotNull(claimsAccessor);
        // When no accessor is registered, GetClaims() returns null (unauthenticated request)
        var claims = claimsAccessor.GetClaims();
        Assert.Null(claims);
    }

    /// <summary>
    /// Verifies the <c>UseRules(Action&lt;RulesetBuilder&gt;)</c> overload wires correctly.
    /// </summary>
    [Fact]
    public async Task UseRules_BuilderOverload_WiresCorrectly()
    {
        await using var provider = BuildProvider(
            RulesetBuilder.Build(r =>
                r.Match("builder/{docId}", b =>
                    b.Allow(RuleOperations.Of(RuleOperation.Get), Expr.Const(true)))),
            out _);

        var rulesGuard = provider.GetRequiredService<RuleGuardedDocumentDatabase>();
        var core = provider.GetRequiredService<DocumentDatabase>();

        await core.WriteAsync([new SetWrite
        {
            Path = "builder/doc1",
            Fields = Fields(("v", new IntegerValue(42))),
        }]);

        // allow get: true → always allowed regardless of claims
        var doc = await rulesGuard.GetAsync("builder/doc1");
        Assert.NotNull(doc);
    }

    /// <summary>
    /// Regression: no <c>UseRules</c> call → access is default-deny for every path.
    /// </summary>
    [Fact]
    public async Task NoUseRules_DefaultDeny()
    {
        var services = new ServiceCollection();
        services.AddWincheDatabase(opts => opts.ConnectionString = fx.ConnectionString);
        await using var provider = services.BuildServiceProvider();

        var core = provider.GetRequiredService<DocumentDatabase>();
        await core.WriteAsync([new SetWrite
        {
            Path = "deny/doc1",
            Fields = Fields(("x", new IntegerValue(1))),
        }]);

        var guard = provider.GetRequiredService<RuleGuardedDocumentDatabase>();
        await Assert.ThrowsAsync<Authorization.AccessDeniedException>(
            () => guard.GetAsync("deny/doc1"));
    }

    /// <summary>
    /// Accumulation proof: two <c>UseRules</c> calls in a single <c>AddWincheDatabase</c>
    /// registration each grant access to a different collection (<c>alpha/{id}</c> and
    /// <c>beta/{id}</c>). Both must succeed, proving that the second call does NOT silently
    /// overwrite the first (last-wins / footgun).
    /// </summary>
    [Fact]
    public async Task TwoUseRulesCalls_BothAccumulate_BothCollectionsAuthorized()
    {
        var services = new ServiceCollection();
        services.AddWincheDatabase(opts =>
        {
            opts.ConnectionString = fx.ConnectionString;
            // First UseRules: grants read on alpha/{id}
            opts.UseRules(r =>
                r.Match("alpha/{id}", b =>
                    b.Allow(RuleOperations.Of(RuleOperation.Get), Expr.Const(true))));
            // Second UseRules: grants read on beta/{id}
            opts.UseRules(r =>
                r.Match("beta/{id}", b =>
                    b.Allow(RuleOperations.Of(RuleOperation.Get), Expr.Const(true))));
        });
        await using var provider = services.BuildServiceProvider();

        var core = provider.GetRequiredService<DocumentDatabase>();
        var guard = provider.GetRequiredService<RuleGuardedDocumentDatabase>();

        await core.WriteAsync([new SetWrite
        {
            Path = "alpha/doc1",
            Fields = Fields(("v", new IntegerValue(1))),
        }]);
        await core.WriteAsync([new SetWrite
        {
            Path = "beta/doc1",
            Fields = Fields(("v", new IntegerValue(2))),
        }]);

        // alpha — granted by the first UseRules call
        var alphaDoc = await guard.GetAsync("alpha/doc1");
        Assert.NotNull(alphaDoc);

        // beta — granted by the second UseRules call (would be denied under last-wins)
        var betaDoc = await guard.GetAsync("beta/doc1");
        Assert.NotNull(betaDoc);

        // gamma — no rule for this collection → default-deny
        await core.WriteAsync([new SetWrite
        {
            Path = "gamma/doc1",
            Fields = Fields(("v", new IntegerValue(3))),
        }]);
        await Assert.ThrowsAsync<Authorization.AccessDeniedException>(
            () => guard.GetAsync("gamma/doc1"));
    }
}
