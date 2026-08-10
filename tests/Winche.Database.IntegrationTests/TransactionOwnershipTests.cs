using Microsoft.Extensions.DependencyInjection;
using Winche.Database.Authorization;
using Winche.Database.DependencyInjection;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Transactions;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;
using Winche.Rules;
using Winche.Rules.Expressions;

namespace Winche.Database.IntegrationTests;

/// <summary>
/// Integration tests for <see cref="RuleGuardedDocumentDatabase"/>'s transaction-ownership guard
/// (security hardening): transaction ids are otherwise unowned bearer tokens — anyone who learns
/// or guesses another caller's id could commit or roll back a transaction they never began. The
/// guard binds each id to the caller who began it and rejects operations from any other caller.
/// <para>
/// Ownership state lives inside a single <see cref="RuleGuardedDocumentDatabase"/> instance, so
/// "different callers" is simulated with one guard resolved from DI plus a
/// <see cref="MutableClaimsAccessor"/> whose claims are swapped between calls — exactly the
/// pattern <see cref="RuleGuardedDatabaseDiTests"/> uses for the same reason.
/// </para>
/// The configured ruleset allows every read/write regardless of claims
/// (<see cref="RuleOperations.All"/> / <c>Expr.Const(true)</c>), so every failure observed here
/// comes from the ownership guard, not from access-rule denial.
/// </summary>
[Collection("postgres")]
public class TransactionOwnershipTests(PostgresFixture fx) : IAsyncLifetime
{
    public Task InitializeAsync() => fx.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static Dictionary<string, Value> Fields(params (string K, Value V)[] entries) =>
        entries.ToDictionary(e => e.K, e => e.V);

    private sealed class MutableClaimsAccessor : IRuleClaimsAccessor
    {
        private IReadOnlyDictionary<string, object?>? _claims;
        public void SetClaims(IReadOnlyDictionary<string, object?>? claims) => _claims = claims;
        public IReadOnlyDictionary<string, object?>? GetClaims() => _claims;
    }

    private ServiceProvider BuildProvider(out MutableClaimsAccessor accessor)
    {
        var mutableAccessor = new MutableClaimsAccessor();
        accessor = mutableAccessor;

        var services = new ServiceCollection();
        services.AddWincheDatabase(opts =>
        {
            opts.ConnectionString = fx.ConnectionString;
            // Allow-all: isolates the ownership guard from access-rule denial.
            opts.UseRules(r => r.Match("{document=**}", b => b.Allow(RuleOperations.All, Expr.Const(true))));
        });
        services.AddSingleton<IRuleClaimsAccessor>(mutableAccessor);
        return services.BuildServiceProvider();
    }

    private static IReadOnlyDictionary<string, object?> Uid(string uid) => new Dictionary<string, object?> { ["uid"] = uid };

    // ── Commit: cross-owner mismatch ────────────────────────────────────────────

    /// <summary>
    /// Caller B ("bob") attempts to commit caller A's ("alice") transaction: rejected with
    /// <see cref="TransactionAbortedException"/>. A's subsequent commit succeeds, proving B's
    /// attempt neither consumed nor damaged the transaction.
    /// </summary>
    [Fact]
    public async Task Commit_ByDifferentOwner_Throws_ThenOriginalOwnerCommitsSuccessfully()
    {
        await using var provider = BuildProvider(out var accessor);
        var guard = provider.GetRequiredService<RuleGuardedDocumentDatabase>();
        var path = $"txown/{Guid.NewGuid():N}";

        accessor.SetClaims(Uid("alice"));
        var tx = await guard.BeginTransactionAsync();

        accessor.SetClaims(Uid("bob"));
        var ex = await Assert.ThrowsAsync<TransactionAbortedException>(
            () => guard.CommitTransactionAsync(tx.Id, [new SetWrite { Path = path, Fields = Fields(("v", new IntegerValue(1))) }]));
        Assert.Equal($"Transaction '{tx.Id}' is not active.", ex.Message);

        // Bob's failed attempt did not consume or damage the transaction — alice can still commit.
        accessor.SetClaims(Uid("alice"));
        var results = await guard.CommitTransactionAsync(tx.Id, [new SetWrite { Path = path, Fields = Fields(("v", new IntegerValue(1))) }]);
        Assert.Single(results);

        var doc = await guard.GetAsync(path);
        Assert.NotNull(doc);
        Assert.Equal(new IntegerValue(1), doc!.Fields["v"]);
    }

    // ── Transactional read: cross-owner mismatch ────────────────────────────────

    /// <summary>Same cross-owner rejection for a transactional read (<c>tx:get</c>).</summary>
    [Fact]
    public async Task Get_ByDifferentOwner_Throws_ThenOriginalOwnerReadsAndCommitsSuccessfully()
    {
        await using var provider = BuildProvider(out var accessor);
        var guard = provider.GetRequiredService<RuleGuardedDocumentDatabase>();
        var path = $"txown/{Guid.NewGuid():N}";
        await guard.WriteAsync([new SetWrite { Path = path, Fields = Fields(("v", new IntegerValue(1))) }]);

        accessor.SetClaims(Uid("alice"));
        var tx = await guard.BeginTransactionAsync();

        accessor.SetClaims(Uid("bob"));
        var ex = await Assert.ThrowsAsync<TransactionAbortedException>(
            () => guard.GetAsync(tx.Id, path));
        Assert.Equal($"Transaction '{tx.Id}' is not active.", ex.Message);

        // Bob's failed read did not consume or damage the transaction — alice can still read+commit.
        accessor.SetClaims(Uid("alice"));
        var doc = await guard.GetAsync(tx.Id, path);
        Assert.NotNull(doc);
        var results = await guard.CommitTransactionAsync(tx.Id, []);
        Assert.Empty(results);
    }

    // ── Rollback: cross-owner mismatch is a silent no-op ────────────────────────

    /// <summary>
    /// Caller B's rollback of caller A's live transaction is a silent no-op (not an exception,
    /// and not a real rollback) — the ledger already treats rollback of an unknown id as
    /// idempotent, and rolling back someone else's LIVE transaction would be a denial-of-service
    /// on its true owner, so the guard must not do that either. A can still commit afterward.
    /// </summary>
    [Fact]
    public async Task Rollback_ByDifferentOwner_IsSilentNoOp_ThenOriginalOwnerCommitsSuccessfully()
    {
        await using var provider = BuildProvider(out var accessor);
        var guard = provider.GetRequiredService<RuleGuardedDocumentDatabase>();
        var path = $"txown/{Guid.NewGuid():N}";

        accessor.SetClaims(Uid("alice"));
        var tx = await guard.BeginTransactionAsync();

        accessor.SetClaims(Uid("bob"));
        await guard.RollbackTransactionAsync(tx.Id);   // must not throw, must not consume the transaction

        accessor.SetClaims(Uid("alice"));
        var results = await guard.CommitTransactionAsync(tx.Id, [new SetWrite { Path = path, Fields = Fields(("v", new IntegerValue(1))) }]);
        Assert.Single(results);
    }

    // ── Oracle check: mismatch is indistinguishable from a made-up id ──────────

    /// <summary>
    /// A caller cannot tell "this transaction exists but isn't mine" apart from "this transaction
    /// does not exist": both produce the identical exception type and message shape.
    /// </summary>
    [Fact]
    public async Task CrossOwnerMismatch_IsIndistinguishableFrom_UnknownTransactionId()
    {
        await using var provider = BuildProvider(out var accessor);
        var guard = provider.GetRequiredService<RuleGuardedDocumentDatabase>();

        accessor.SetClaims(Uid("alice"));
        var tx = await guard.BeginTransactionAsync();

        accessor.SetClaims(Uid("bob"));
        var mismatchEx = await Assert.ThrowsAsync<TransactionAbortedException>(
            () => guard.CommitTransactionAsync(tx.Id, []));

        var madeUpId = Guid.NewGuid().ToString("N");
        var unknownEx = await Assert.ThrowsAsync<TransactionAbortedException>(
            () => guard.CommitTransactionAsync(madeUpId, []));

        // Same exception type, and the same message TEMPLATE ("Transaction '{id}' is not active.")
        // with only the id substituted — a caller learns nothing about which case it hit.
        Assert.Equal(mismatchEx.GetType(), unknownEx.GetType());
        Assert.Equal($"Transaction '{tx.Id}' is not active.", mismatchEx.Message);
        Assert.Equal($"Transaction '{madeUpId}' is not active.", unknownEx.Message);
    }

    // ── Anonymous callers: documented limitation ────────────────────────────────

    /// <summary>
    /// KNOWN LIMITATION, recorded deliberately (not an accident): callers with no usable <c>uid</c>
    /// have no identity to bind a transaction to, so ALL anonymous callers share ONE owner bucket.
    /// Two different anonymous callers are therefore indistinguishable to the guard — an anonymous
    /// transaction is protected only by its id being an unguessable GUID, not by caller identity.
    /// This test asserts that behavior explicitly so a future change to it is a deliberate decision.
    /// </summary>
    [Fact]
    public async Task AnonymousCallers_ShareOneOwnerBucket_KnownLimitation()
    {
        await using var provider = BuildProvider(out var accessor);
        var guard = provider.GetRequiredService<RuleGuardedDocumentDatabase>();
        var path = $"txown/{Guid.NewGuid():N}";

        accessor.SetClaims(null);   // anonymous caller #1 begins the transaction
        var tx = await guard.BeginTransactionAsync();

        accessor.SetClaims(new Dictionary<string, object?>());   // anonymous caller #2 (empty claims — also no usable uid)
        // Not rejected: both are bucketed under the same anonymous owner key, so this is
        // indistinguishable from the SAME anonymous caller continuing its own transaction.
        var results = await guard.CommitTransactionAsync(tx.Id, [new SetWrite { Path = path, Fields = Fields(("v", new IntegerValue(1))) }]);
        Assert.Single(results);
    }

    // ── Control: single-identity round trip still works ─────────────────────────

    /// <summary>Normal single-identity transaction round trip is unaffected by the ownership guard.</summary>
    [Fact]
    public async Task SingleIdentity_RoundTrip_StillWorksEndToEnd()
    {
        await using var provider = BuildProvider(out var accessor);
        var guard = provider.GetRequiredService<RuleGuardedDocumentDatabase>();
        var path = $"txown/{Guid.NewGuid():N}";

        accessor.SetClaims(Uid("alice"));
        var tx = await guard.BeginTransactionAsync();
        var before = await guard.GetAsync(tx.Id, path);   // recorded read; missing doc is fine
        Assert.Null(before);

        var results = await guard.CommitTransactionAsync(tx.Id, [new SetWrite { Path = path, Fields = Fields(("v", new IntegerValue(42))) }]);
        Assert.Single(results);

        var after = await guard.GetAsync(path);
        Assert.NotNull(after);
        Assert.Equal(new IntegerValue(42), after!.Fields["v"]);
    }
}
