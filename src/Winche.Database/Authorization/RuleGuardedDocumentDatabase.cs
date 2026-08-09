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
/// </remarks>
public sealed class RuleGuardedDocumentDatabase(
    IDocumentDatabase inner,
    RuleEngine engine,
    IRuleClaimsAccessor claimsAccessor)
    : IDocumentDatabase
{
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

    public Task<TransactionHandle> BeginTransactionAsync(CancellationToken ct = default) =>
        inner.BeginTransactionAsync(ct);

    public async Task<Document?> GetAsync(string transactionId, string path, CancellationToken ct = default)
    {
        // Mirror the non-transactional get: read, then authorize against the read document.
        // Denied → throw before returning, so the caller never sees a document it cannot read.
        var doc = await inner.GetAsync(transactionId, path, ct);
        await AuthorizeGetAsync(path, doc, ct);
        return doc;
    }

    public Task<QueryResult> QueryAsync(string transactionId, Query query, CancellationToken ct = default)
    {
        AuthorizeListQuery(query);
        return inner.QueryAsync(transactionId, query, ct);
    }

    public Task<IReadOnlyList<WriteResult>> CommitTransactionAsync(
        string transactionId, IReadOnlyList<Write> writes, CancellationToken ct = default) =>
        inner.CommitTransactionAsync(transactionId, writes, ct);   // authorized inside the write transaction

    public Task RollbackTransactionAsync(string transactionId, CancellationToken ct = default) =>
        inner.RollbackTransactionAsync(transactionId, ct);

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
}
