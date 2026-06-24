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
/// </summary>
/// <remarks>
/// Constructor arguments are intentionally decoupled from the transport layer:
/// <paramref name="claimsProvider"/> is a <see cref="Func{T}"/> so the caller controls how auth
/// claims are sourced (HTTP context, test fixture, etc.).
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
        // Authorize each path individually as a get. Load via inner to get current document state
        // for resource, then check rules. Any single denial throws before returning results.
        foreach (var path in paths)
        {
            var doc = await inner.GetAsync(path, ct);
            await AuthorizeGetAsync(path, doc, ct);
        }
        return await inner.GetAllAsync(paths, ct);
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
        // TODO Phase 2/3: thread authorization through TransactionRunner using this guard
        inner.RunTransactionAsync(body, options, ct);

    /// <summary>
    /// Authorizes the subscription query at subscribe time using the same
    /// <see cref="QueryToConstraints"/>-based analyzer as <see cref="QueryAsync"/>.
    /// If the query is provably safe, every document the live stream can ever
    /// return already satisfies the read rule (the query's own constraints
    /// guarantee it), so no per-document filtering is applied to snapshots.
    /// If the query is not provably safe the subscription is rejected outright
    /// with <see cref="AccessDeniedException"/> — the "rules are not filters"
    /// principle applied to listeners.
    /// <para>
    /// <b>Limitation:</b> authorization is evaluated once against the
    /// subscribe-time claims. If the caller's auth token is refreshed or
    /// revoked after the listener is open the subscription is NOT re-checked.
    /// Re-authorization on token refresh is out of scope for Phase 3 and
    /// should be addressed in a future phase.
    /// </para>
    /// </summary>
    public IQueryListener Listen(Query query, ListenOptions? options = null)
    {
        AuthorizeListQuery(query);           // throws AccessDeniedException at subscribe time if not provably safe
        return inner.Listen(query, options); // provably safe → every live result is authorized; no per-doc filtering
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
