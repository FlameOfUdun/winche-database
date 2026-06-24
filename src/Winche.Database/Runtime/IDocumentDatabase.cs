using Winche.Database.Documents;
using Winche.Database.Querying;
using Winche.Database.Querying.Ast;
using Winche.Database.Runtime.Listening;
using Winche.Database.Runtime.Transactions;
using Winche.Database.Runtime.Writes;
using Winche.Database.Values;

namespace Winche.Database.Runtime;

/// <summary>The spec-faithful runtime surface (spec §1).</summary>
public interface IDocumentDatabase
{
    Task<Document?> GetAsync(string path, CancellationToken ct = default);
    Task<IReadOnlyList<Document?>> GetAllAsync(IReadOnlyList<string> paths, CancellationToken ct = default);
    Task<QueryResult> QueryAsync(Query query, CancellationToken ct = default);
    Task<long> CountAsync(Query query, CancellationToken ct = default);

    /// <summary>Runs aggregations (count/sum/average) over the query in one call (the aggregate() operation).</summary>
    Task<AggregationResult> AggregateAsync(Query query, IReadOnlyList<Aggregation> aggregations, CancellationToken ct = default);

    Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<Write> writes, CancellationToken ct = default);

    /// <summary>
    /// Creates a new document in <paramref name="collectionPath"/> with a generated 20-char id
    /// (the add() create operation). Returns the created document. Enforced as a create by the write authorizer.
    /// </summary>
    Task<Document> AddAsync(string collectionPath, IReadOnlyDictionary<string, Value> fields, CancellationToken ct = default);

    Task<TransactionHandle> BeginTransactionAsync(CancellationToken ct = default);
    Task<Document?> GetAsync(string transactionId, string path, CancellationToken ct = default);
    Task<QueryResult> QueryAsync(string transactionId, Query query, CancellationToken ct = default);
    Task<IReadOnlyList<WriteResult>> CommitTransactionAsync(string transactionId, IReadOnlyList<Write> writes, CancellationToken ct = default);
    Task RollbackTransactionAsync(string transactionId, CancellationToken ct = default);
    Task<T> RunTransactionAsync<T>(Func<TransactionContext, Task<T>> body, TransactionOptions? options = null, CancellationToken ct = default);

    IQueryListener Listen(Query query, ListenOptions? options = null);

    /// <summary>
    /// Listens to a single document by path (the single-document snapshot contract). Rides on the query
    /// listener over a __name__ == path query; emits a DocumentSnapshot per change (present/absent).
    /// <para>
    /// Authorization rides on <see cref="Listen"/>, so the caller's rules must permit <c>list</c> on
    /// the parent collection — not the per-document <c>get</c> rule (an intentional phase
    /// divergence; see PROTOCOL). Any concrete override MUST also authorize via <see cref="Listen"/>.
    /// </para>
    /// </summary>
    IDocumentListener ListenToDocument(string path, ListenOptions? options = null) =>
        new DocumentListener(Listen(DocumentListenQuery.For(path), options));
}
