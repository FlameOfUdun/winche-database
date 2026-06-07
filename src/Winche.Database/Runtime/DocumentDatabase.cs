using Microsoft.Extensions.Options;
using Npgsql;
using Winche.Database.Documents;
using Winche.Database.Models;
using Winche.Database.Querying;
using Winche.Database.Querying.Ast;
using Winche.Database.Runtime.Listening;
using Winche.Database.Runtime.Transactions;
using Winche.Database.Runtime.Writes;

namespace Winche.Database.Runtime;

/// <summary>
/// The rule-free core runtime (spec architecture): reads via the engine executors,
/// every mutation via WriteApplier, transactions via the ledger + read-validations.
/// Access rules live in GuardedDocumentDatabase; hooks/listeners consume the change feed (Plan 3).
/// </summary>
public sealed class DocumentDatabase : IDocumentDatabase
{
    private readonly NpgsqlDataSource _source;
    private readonly string _table;
    private readonly WriteApplier _applier;
    private readonly TransactionLedger _ledger;
    private readonly ListenerRegistry? _listeners;

    public DocumentDatabase(NpgsqlDataSource source, IOptions<StoreOptions> options, ListenerRegistry? listeners = null)
    {
        _source = source;
        _table = options.Value.TableName;
        _applier = new WriteApplier(source, _table);
        _ledger = new TransactionLedger(options.Value.TransactionConfig);
        _listeners = listeners;
    }

    /// <summary>Exposed for the Plan-3 expiry sweeper.</summary>
    public TransactionLedger Ledger => _ledger;

    // ── Reads ─────────────────────────────────────────────────────────────────

    public async Task<Document?> GetAsync(string path, CancellationToken ct = default)
    {
        await using var conn = await _source.OpenConnectionAsync(ct);
        return await new DocumentOperations(conn, null, _table).GetAsync(path, ct);
    }

    public async Task<IReadOnlyList<Document?>> GetAllAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        await using var conn = await _source.OpenConnectionAsync(ct);
        var ops = new DocumentOperations(conn, null, _table);
        var docs = new List<Document?>(paths.Count);
        foreach (var path in paths)
            docs.Add(await ops.GetAsync(path, ct));
        return docs;                                              // input order preserved
    }

    public async Task<QueryResult> QueryAsync(QueryAst query, CancellationToken ct = default)
    {
        await using var conn = await _source.OpenConnectionAsync(ct);
        return await new QueryExecutor(conn, null, _table).ExecuteAsync(query, ct);
    }

    public async Task<PipelineResult> AggregateAsync(PipelineAst pipeline, CancellationToken ct = default)
    {
        await using var conn = await _source.OpenConnectionAsync(ct);
        return await new PipelineExecutor(conn, null, _table).ExecuteAsync(pipeline, ct);
    }

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

    public async Task<QueryResult> QueryAsync(string transactionId, QueryAst query, CancellationToken ct = default)
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

    public IQueryListener Listen(QueryAst query, ListenOptions? options = null) =>
        _listeners?.Listen(query, options)
        ?? throw new NotSupportedException("This DocumentDatabase was constructed without a ListenerRegistry.");
}
