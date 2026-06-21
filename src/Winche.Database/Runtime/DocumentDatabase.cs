using Microsoft.Extensions.Options;
using Npgsql;
using Winche.Database.Documents;
using Winche.Database.DependencyInjection;
using Winche.Database.Querying;
using Winche.Database.Querying.Ast;
using Winche.Database.Runtime.Listening;
using Winche.Database.Runtime.Transactions;
using Winche.Database.Runtime.Writes;

namespace Winche.Database.Runtime;

/// <summary>
/// The rule-free core runtime (spec architecture): reads via the engine executors,
/// every mutation via WriteApplier, transactions via the ledger + read-validations.
/// Access rules live in RuleGuardedDocumentDatabase; hooks/listeners consume the change feed (Plan 3).
/// </summary>
public sealed class DocumentDatabase : IDocumentDatabase
{
    private readonly NpgsqlDataSource _source;
    private readonly WriteApplier _applier;
    private readonly TransactionLedger _ledger;
    private readonly ListenerRegistry? _listeners;
    private readonly CollectionIndexResolver? _scopes;

    public DocumentDatabase(NpgsqlDataSource source, IOptions<WincheDatabaseOptions> options,
        ListenerRegistry? listeners = null, CollectionIndexResolver? scopes = null,
        IWriteAuthorizer? writeAuthorizer = null)
    {
        _source = source;
        _applier = new WriteApplier(source, writeAuthorizer);
        _ledger = new TransactionLedger(options.Value.TransactionConfig);
        _listeners = listeners;
        _scopes = scopes;
    }

    /// <summary>Exposed for the Plan-3 expiry sweeper.</summary>
    public TransactionLedger Ledger => _ledger;

    // ── Reads ─────────────────────────────────────────────────────────────────

    public async Task<Document?> GetAsync(string path, CancellationToken ct = default)
    {
        await using var conn = await _source.OpenConnectionAsync(ct);
        return await new DocumentOperations(conn, null).GetAsync(path, ct);
    }

    public async Task<IReadOnlyList<Document?>> GetAllAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        // Validate each path before querying — mirrors the per-path validation in GetAsync
        // (DocumentOperations.GetAsync calls ValidateDocumentPath → ArgumentException).
        foreach (var path in paths)
        {
            if (!DocumentPathParser.IsValidDocumentPath(path, out var error))
                throw new ArgumentException(error);
        }
        await using var conn = await _source.OpenConnectionAsync(ct);
        var map = await new DocumentOperations(conn, null).GetManyAsync(paths.Distinct(StringComparer.Ordinal).ToList(), ct);
        return [.. paths.Select(p => map.GetValueOrDefault(p))];
    }

    public async Task<QueryResult> QueryAsync(Query query, CancellationToken ct = default)
    {
        await using var conn = await _source.OpenConnectionAsync(ct);
        return await new QueryExecutor(conn, null, _scopes).ExecuteAsync(query, ct);
    }

    public async Task<long> CountAsync(Query query, CancellationToken ct = default)
    {
        await using var conn = await _source.OpenConnectionAsync(ct);
        return await new QueryExecutor(conn, null, _scopes).CountAsync(query, ct);
    }

    /// <summary>
    /// Lists the ids of subcollections directly under <paramref name="parentDocumentPath"/>
    /// (or the top-level collections when null/empty). Privileged/internal: this is exposed
    /// only on the rule-free <see cref="DocumentDatabase"/>, never through IDocumentDatabase —
    /// mirroring Firestore, where listCollectionIds is an Admin-SDK-only operation that
    /// security rules never evaluate. Ids are distinct and ordered by UTF-8 byte order.
    /// </summary>
    public async Task<ListCollectionIdsResult> ListCollectionIdsAsync(
        string? parentDocumentPath, int? pageSize = null, string? pageToken = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(parentDocumentPath)
            && !DocumentPathParser.IsValidDocumentPath(parentDocumentPath, out var error))
            throw new ArgumentException(error);

        var size = NormalizePageSize(pageSize);
        var after = pageToken is null ? null : CollectionPageToken.Decode(pageToken);

        await using var conn = await _source.OpenConnectionAsync(ct);
        // Fetch one extra row to detect whether another page exists.
        var ids = await new CollectionLister(conn, null).ListAsync(parentDocumentPath, after, size + 1, ct);

        if (ids.Count <= size)
            return new ListCollectionIdsResult(ids, null);

        var page = ids.Take(size).ToList();
        return new ListCollectionIdsResult(page, CollectionPageToken.Encode(page[^1]));
    }

    private const int DefaultPageSize = 100;
    private const int MaxPageSize = 300;

    private static int NormalizePageSize(int? pageSize) =>
        pageSize is null or <= 0 ? DefaultPageSize : Math.Min(pageSize.Value, MaxPageSize);

    // ── Writes ────────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<Write> writes, CancellationToken ct = default) =>
        _applier.ApplyAsync(writes, ct: ct);

    // ── Transactions ──────────────────────────────────────────────────────────

    public Task<TransactionHandle> BeginTransactionAsync(CancellationToken ct = default) =>
        Task.FromResult(new TransactionHandle(_ledger.Begin()));

    public async Task<Document?> GetAsync(string transactionId, string path, CancellationToken ct = default)
    {
        var doc = await GetAsync(path, ct);
        _ledger.RecordRead(transactionId, path, doc?.UpdateTime);
        return doc;
    }

    public async Task<QueryResult> QueryAsync(string transactionId, Query query, CancellationToken ct = default)
    {
        var result = await QueryAsync(query, ct);
        foreach (var doc in result.Documents)
            _ledger.RecordRead(transactionId, doc.Path, doc.UpdateTime);
        return result;
    }

    public async Task<IReadOnlyList<WriteResult>> CommitTransactionAsync(
        string transactionId, IReadOnlyList<Write> writes, CancellationToken ct = default)
    {
        var readSet = _ledger.Consume(transactionId);
        try
        {
            return await _applier.ApplyAsync(writes, readSet, ct);
        }
        catch (RuntimeException ex) when (ex.Status == RuntimeStatus.Aborted && ex is not TransactionAbortedException)
        {
            throw new TransactionAbortedException(ex.Message);
        }
    }

    public Task RollbackTransactionAsync(string transactionId, CancellationToken ct = default)
    {
        _ledger.Rollback(transactionId);
        return Task.CompletedTask;
    }

    public Task<T> RunTransactionAsync<T>(Func<TransactionContext, Task<T>> body,
        TransactionOptions? options = null, CancellationToken ct = default) =>
        TransactionRunner.RunAsync(this, body, options, ct);

    // ── Listeners (Plan 3) ────────────────────────────────────────────────────

    public IQueryListener Listen(Query query, ListenOptions? options = null) =>
        _listeners?.Listen(query, options)
        ?? throw new NotSupportedException("This DocumentDatabase was constructed without a ListenerRegistry.");
}
