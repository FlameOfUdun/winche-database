using Winche.Database.Documents;
using Winche.Database.Querying;
using Winche.Database.Querying.Ast;
using Winche.Database.Runtime;
using Winche.Database.Runtime.Listening;
using Winche.Database.Runtime.Transactions;
using Winche.Database.Runtime.Writes;

namespace Winche.Database.Tests.Runtime;

/// <summary>Every member throws unless overridden — unit tests override exactly what they exercise.</summary>
public abstract class DatabaseTestDouble : IDocumentDatabase
{
    public virtual Task<Document?> GetAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<IReadOnlyList<Document?>> GetAllAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<QueryResult> QueryAsync(Query query, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<PipelineResult> AggregateAsync(Pipeline pipeline, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<IReadOnlyList<WriteResult>> WriteAsync(IReadOnlyList<Write> writes, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<TransactionHandle> BeginTransactionAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<Document?> GetAsync(string transactionId, string path, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<QueryResult> QueryAsync(string transactionId, Query query, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<IReadOnlyList<WriteResult>> CommitTransactionAsync(string transactionId, IReadOnlyList<Write> writes, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task RollbackTransactionAsync(string transactionId, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual Task<T> RunTransactionAsync<T>(Func<TransactionContext, Task<T>> body, TransactionOptions? options = null, CancellationToken ct = default) => throw new NotImplementedException();
    public virtual IQueryListener Listen(Query query, ListenOptions? options = null) => throw new NotImplementedException();
}
