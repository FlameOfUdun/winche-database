using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Winche.Database.DependencyInjection;
using Winche.Database.Documents;
using Winche.Database.Querying;
using Winche.Database.Querying.Ast;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Listening;
using Winche.Database.Runtime.Transactions;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;
using Winche.Rules;
using Winche.Rules.Evaluation;

namespace Winche.Database.Authorization;

/// <summary>
/// <see cref="RuleEngine"/>-based authorization decorator over the rule-free core <see cref="IDocumentDatabase"/>.
/// Enforces read authorization (get, list, listen) via the injected <see cref="RuleEngine"/>.
/// Write authorization (create, update, delete) is handled inside the write transaction by
/// <see cref="IWriteAuthorizer"/> injected into the core <see cref="DocumentDatabase"/> — this
/// decorator simply delegates <c>WriteAsync</c> and <c>CommitTransactionAsync</c> to the inner core.
/// <c>RunTransactionAsync</c> runs through this guard, so reads inside the transaction body are
/// authorized as get/list just like non-transactional reads.
/// </summary>
/// <remarks>
/// Constructor arguments are intentionally decoupled from the transport layer:
/// <paramref name="claimsAccessor"/> is an <see cref="IRuleClaimsAccessor"/> so the caller controls
/// how auth claims are sourced (HTTP context, test fixture, etc.).
/// <para>
/// <b>Transaction ownership.</b> This is also the only layer that knows caller identity, so it is
/// the right place to close a bearer-token gap: without it, any caller who learns or guesses
/// another caller's transaction id could commit or roll back a transaction they never began — REST
/// has no per-connection gate (unlike the WebSocket transport's <c>ConnectionScope.Transactions</c>).
/// This decorator records the caller's owner key (see <see cref="OwnerKey"/>) for every id it mints
/// in <see cref="BeginTransactionAsync"/> and verifies it on every subsequent
/// transactional operation — see <see cref="VerifyOwner"/> and <see cref="RollbackTransactionAsync"/>
/// for exactly what "verify" means on each operation.
/// </para>
/// </remarks>
public sealed class RuleGuardedDocumentDatabase(
    IDocumentDatabase inner,
    RuleEngine engine,
    IRuleClaimsAccessor claimsAccessor,
    IOptions<WincheDatabaseOptions> options)
    : IDocumentDatabase
{
    /// <summary>
    /// Transaction id → (owner key, begun-at). Populated by <see cref="BeginTransactionAsync"/> for
    /// every id this guard hands out; consulted by every other transactional operation. An id with
    /// NO entry here delegates through unchanged — see the "unrecorded id" reasoning on
    /// <see cref="VerifyOwner"/>.
    /// <para>
    /// A null owner key means "no usable uid" (see <see cref="OwnerKey"/>). Null rather than a
    /// sentinel string on purpose: a uid is a caller-supplied value, so any in-band sentinel could
    /// in principle be supplied as a real uid and collide with the anonymous bucket.
    /// </para>
    /// </summary>
    private readonly ConcurrentDictionary<string, (string? OwnerKey, DateTimeOffset BegunAt)> _owners = new();


    // ── Reads ─────────────────────────────────────────────────────────────────

    public async Task<Document?> GetAsync(string path, CancellationToken ct = default)
    {
        var doc = await inner.GetAsync(path, ct);
        await AuthorizeGetAsync(path, doc, ct);
        return doc;
    }

    public async Task<IReadOnlyList<Document?>> GetAllAsync(
        IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        // One batch read; authorize each returned document against the SAME snapshot it returns
        // (eliminates N individual reads and the read-twice TOCTOU). Any single denial throws
        // before returning, so the caller never sees a document it cannot read.
        var docs = await inner.GetAllAsync(paths, ct);
        for (var i = 0; i < paths.Count; i++)
            await AuthorizeGetAsync(paths[i], docs[i], ct);
        return docs;
    }

    public async Task<QueryResult> QueryAsync(Query query, CancellationToken ct = default)
    {
        AuthorizeListQuery(query);
        return await inner.QueryAsync(query, ct);
    }

    public async Task<long> CountAsync(Query query, CancellationToken ct = default)
    {
        AuthorizeListQuery(query);
        return await inner.CountAsync(query, ct);
    }

    public async Task<AggregationResult> AggregateAsync(Query query, IReadOnlyList<Aggregation> aggregations, CancellationToken ct = default)
    {
        AuthorizeListQuery(query);
        return await inner.AggregateAsync(query, aggregations, ct);
    }

    // ── Writes ────────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<WriteResult>> WriteAsync(
        IReadOnlyList<Write> writes, CancellationToken ct = default) =>
        inner.WriteAsync(writes, ct);   // authorized inside the write transaction by IWriteAuthorizer

    public Task<Document> AddAsync(string collectionPath, IReadOnlyDictionary<string, Value> fields, CancellationToken ct = default) =>
        inner.AddAsync(collectionPath, fields, ct);   // create authorized inside the write transaction by IWriteAuthorizer

    // ── Transactions ──────────────────────────────────────────────────────────

    public async Task<TransactionHandle> BeginTransactionAsync(CancellationToken ct = default)
    {
        // Amortized maintenance: prune stale owner entries here rather than on a timer, so the
        // map cannot grow without bound. Safe against the ledger's own absolute lifetime cap
        // (TransactionConfig.TotalTimeoutSpan) — an entry is only pruned once the ledger itself
        // would already treat that id as expired, so we never drop an owner record the ledger
        // still considers live.
        PruneExpiredOwners();
        var handle = await inner.BeginTransactionAsync(ct);
        _owners[handle.Id] = (OwnerKey(), DateTimeOffset.UtcNow);
        return handle;
    }

    public async Task<Document?> GetAsync(string transactionId, string path, CancellationToken ct = default)
    {
        VerifyOwner(transactionId);
        // Mirror the non-transactional get: read, then authorize against the read document.
        // Denied → throw before returning, so the caller never sees a document it cannot read.
        var doc = await inner.GetAsync(transactionId, path, ct);
        await AuthorizeGetAsync(path, doc, ct);
        return doc;
    }

    public Task<QueryResult> QueryAsync(string transactionId, Query query, CancellationToken ct = default)
    {
        VerifyOwner(transactionId);
        AuthorizeListQuery(query);
        return inner.QueryAsync(transactionId, query, ct);
    }

    public async Task<IReadOnlyList<WriteResult>> CommitTransactionAsync(
        string transactionId, IReadOnlyList<Write> writes, CancellationToken ct = default)
    {
        VerifyOwner(transactionId);
        try
        {
            return await inner.CommitTransactionAsync(transactionId, writes, ct);   // authorized inside the write transaction
        }
        finally
        {
            // The inner ledger consumes (removes) the id on every CommitTransactionAsync call,
            // success or failure — mirror that here so the owner map never outlives the ledger entry.
            _owners.TryRemove(transactionId, out _);
        }
    }

    public async Task RollbackTransactionAsync(string transactionId, CancellationToken ct = default)
    {
        if (_owners.TryGetValue(transactionId, out var entry))
        {
            if (entry.OwnerKey != OwnerKey())
                return;   // owned by someone else: silent no-op — a real rollback would be a DoS on the true owner
            _owners.TryRemove(transactionId, out _);
        }
        await inner.RollbackTransactionAsync(transactionId, ct);
    }

    public Task<T> RunTransactionAsync<T>(
        Func<TransactionContext, Task<T>> body,
        TransactionOptions? options = null,
        CancellationToken ct = default) =>
        // Run through THIS guard so the body's reads are authorized (get/list) exactly like
        // non-transactional reads; writes remain authorized at commit via IWriteAuthorizer.
        TransactionRunner.RunAsync(this, body, options, ct);

    /// <summary>
    /// Authorizes the subscription query at subscribe time using the same
    /// <see cref="QueryToConstraints"/>-based analyzer as <see cref="QueryAsync"/>.
    /// If the query is not provably safe the subscription is rejected outright
    /// with <see cref="AccessDeniedException"/> — the "rules are not filters"
    /// principle applied to listeners.
    /// <para>
    /// The returned listener re-runs that analysis before every emission, so a ruleset tightened
    /// while the subscription is open (or a rule whose outcome depends on <c>request.time</c>)
    /// terminates the stream instead of continuing to deliver. Claims are captured at subscribe
    /// time — see <see cref="RuleGuardedQueryListener"/> for what that does and does not cover.
    /// </para>
    /// </summary>
    public IQueryListener Listen(Query query, ListenOptions? options = null)
    {
        AuthorizeListQuery(query);           // throws AccessDeniedException at subscribe time if not provably safe
        return new RuleGuardedQueryListener(
            inner.Listen(query, options), engine, inner, query, claimsAccessor.GetClaims());
    }

    /// <summary>
    /// Authorizes a single-document subscription as a <c>get</c> on <paramref name="path"/> — single-document
    /// reads (one-shot or realtime) are governed by the per-document get rule with the full path bound,
    /// never the list rule over the parent collection.
    /// <para>
    /// The returned listener re-evaluates that rule against the current document before every emission
    /// carrying a body, so losing read access mid-subscription terminates the stream rather than
    /// continuing to deliver document contents. Claims are captured at subscribe time — see
    /// <see cref="RuleGuardedDocumentListener"/> for what that does and does not cover.
    /// </para>
    /// </summary>
    public async Task<IDocumentListener> ListenToDocumentAsync(string path, ListenOptions? options = null, CancellationToken ct = default)
    {
        var doc = await inner.GetAsync(path, ct);          // also validates the document path
        await AuthorizeGetAsync(path, doc, ct);            // throws AccessDeniedException(path, "get") if denied
        return new RuleGuardedDocumentListener(
            await inner.ListenToDocumentAsync(path, options, ct), engine, inner, path, claimsAccessor.GetClaims());
    }

    // ── Authorization helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Authorizes a single get operation. Shared between <see cref="GetAsync(string,CancellationToken)"/>
    /// and the per-path loop in <see cref="GetAllAsync"/>.
    /// </summary>
    private async Task AuthorizeGetAsync(string path, Document? doc, CancellationToken ct)
    {
        var resource = doc is not null ? DocumentToResource.Convert(doc) : RuleValue.Null;
        var request = new RuleRequest
        {
            Resource = resource,
            Request = RequestBuilder.Build(claimsAccessor.GetClaims(), "get", RuleValue.Null),
            Provider = new PostgresDocumentSource(inner),
        };
        if (!await engine.AllowsAsync(RuleOperation.Get, path, request, ct))
            throw new AccessDeniedException(path, "get");
    }

    /// <summary>
    /// Authorizes a list/query operation using <see cref="RuleEngine.Allows(Querying.QueryConstraints,RuleRequest)"/>.
    /// Implements the "rules are not filters" principle: the query is either provably safe and
    /// allowed in full, or rejected outright — results are never post-filtered.
    /// </summary>
    private void AuthorizeListQuery(Query query)
    {
        var constraints = QueryToConstraints.Convert(query);
        var request = new RuleRequest
        {
            Request = RequestBuilder.Build(claimsAccessor.GetClaims(), "list", RuleValue.Null),
            Provider = new PostgresDocumentSource(inner),
        };
        if (!engine.Allows(constraints, request))
            throw new AccessDeniedException(query.Collection, "list");
    }

    // ── Transaction-ownership helpers ────────────────────────────────────────

    /// <summary>
    /// The current caller's owner key: the authenticated <c>uid</c> (same rule as
    /// <see cref="RequestBuilder.TryGetUid"/> — present, non-null, non-blank), or <c>null</c>,
    /// the bucket shared by every unauthenticated caller.
    /// <para>
    /// <b>Anonymous callers are a known, documented limitation.</b> With no usable <c>uid</c> there
    /// is no identity to bind a transaction to, so ALL anonymous callers share one bucket and are
    /// protected only by the transaction id being an unguessable GUID minted by
    /// <see cref="Runtime.Transactions.TransactionLedger.Begin"/> — not by caller identity. Any
    /// anonymous caller who learns another anonymous caller's transaction id can still commit or
    /// roll it back. This is intentional (there is nothing else to key on) and not silently
    /// pretended away — see the anonymous-bucket test on <see cref="RuleGuardedDocumentDatabase"/>.
    /// </para>
    /// </summary>
    private string? OwnerKey() =>
        RequestBuilder.TryGetUid(claimsAccessor.GetClaims(), out var uid) ? uid : null;

    /// <summary>
    /// Throws if <paramref name="transactionId"/> is recorded under a DIFFERENT owner than the
    /// current caller. Used by every transactional read and by commit; rollback has its own,
    /// non-throwing variant (see <see cref="RollbackTransactionAsync"/>) because a real rollback of
    /// someone else's live transaction would be a denial-of-service on its true owner.
    /// <para>
    /// An id with NO recorded owner delegates through unchanged — this is load-bearing, not an
    /// oversight: every id this guard ever hands out is recorded in <see cref="BeginTransactionAsync"/>,
    /// so an unrecorded id is either bogus (the inner ledger will itself reject it) or was minted
    /// directly by a privileged, rule-free core server-side (which never goes through this guard's
    /// <see cref="BeginTransactionAsync"/> and so has no owner to check against).
    /// </para>
    /// <para>
    /// On mismatch this throws the IDENTICAL exception type and message the inner ledger already
    /// uses for an unknown/expired id (see <see cref="Runtime.Transactions.TransactionLedger"/>) —
    /// deliberately, so a caller cannot distinguish "exists but isn't yours" from "does not exist".
    /// Revealing that distinction would let a caller probe for the existence of other callers'
    /// transaction ids.
    /// </para>
    /// </summary>
    private void VerifyOwner(string transactionId)
    {
        if (_owners.TryGetValue(transactionId, out var entry) && entry.OwnerKey != OwnerKey())
            throw new TransactionAbortedException($"Transaction '{transactionId}' is not active.");
    }

    /// <summary>
    /// Removes owner entries begun longer ago than <see cref="TransactionConfig.TotalTimeoutSpan"/> —
    /// the ledger's own absolute lifetime cap, so an id this old is already dead there regardless of
    /// idle activity. Called from <see cref="BeginTransactionAsync"/> (amortized cost of every new
    /// transaction) rather than on a timer, keeping this type free of its own background work.
    /// </summary>
    private void PruneExpiredOwners()
    {
        var cutoff = DateTimeOffset.UtcNow - options.Value.TransactionConfig.TotalTimeoutSpan;
        foreach (var (id, entry) in _owners)
            if (entry.BegunAt < cutoff)
                _owners.TryRemove(id, out _);
    }
}
